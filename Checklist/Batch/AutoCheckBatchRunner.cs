using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovCommon.Storage;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.Batch
{
    /// <summary>
    /// Прогон автопроверок без UI (пакетный режим TNovAuto).
    /// Пишет тот же документ autocheck ({modelName},autocheck.json или TNovApi) и логи, что и окно чек-листа.
    /// Не использует RevitAPI.UiApplication, WPF и диалоги.
    /// </summary>
    public static class AutoCheckBatchRunner
    {
        /// <summary>
        /// Не выполняются в пакетном режиме: им нужны открытые рабочие наборы, а TNovAuto открывает
        /// модель отсоединённой без наборов. Их результат в JSON не трогаем — остаётся ручной прогон.
        /// </summary>
        private static readonly HashSet<int> ExcludedInBatch = new HashSet<int>
        {
            AutoCheckStore.RfCoordinationNumber
        };

        /// <param name="modelName">Имя центральной модели без расширения, запятые заменены пробелами.</param>
        /// <param name="projectsFolder">{ServerPath}projects — для файлового режима (в режиме API не используется).</param>
        /// <exception cref="InvalidOperationException">
        /// Сервер TNov недоступен — результаты не сохранены (TNovAuto пишет причину в свой лог).
        /// </exception>
        public static AutoCheckBatchResult Run(Document doc, string modelName, string projectsFolder, string creator)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(modelName)) throw new ArgumentException("Пустое имя модели", nameof(modelName));
            if (string.IsNullOrWhiteSpace(projectsFolder)) throw new ArgumentException("Не задана папка projects", nameof(projectsFolder));

            IDocumentStore store = CreateStore(projectsFolder);
            var attachments = new ChecklistAttachments(store, modelName);
            var result = new AutoCheckBatchResult
            {
                JsonPath = store is FileDocumentStore files
                    ? files.PathOf(DocumentKinds.AutoCheck, modelName)
                    : $"api:{DocumentKinds.AutoCheck}/{modelName}"
            };

            var runs = new List<(int Number, CheckRunResult Run)>();
            foreach (var check in CheckRegistry.ApplicableAutoCheckRunners(modelName)
                         .Where(c => !ExcludedInBatch.Contains(c.Number)))
            {
                CheckRunResult run;
                string error = null;
                try
                {
                    run = check.Run(doc) ?? throw new InvalidOperationException("Проверка вернула null");
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    run = new CheckRunResult
                    {
                        Title = check.Title,
                        Passed = false,
                        ElemIds = "",
                        Log = $"Ошибка выполнения проверки в пакетном режиме: {ex}"
                    };
                }
                runs.Add((check.Number, run));

                result.Items.Add(new AutoCheckBatchItem
                {
                    Number = check.Number,
                    Title = run.Title ?? check.Title,
                    Passed = run.Passed,
                    Error = error
                });
            }

            DateTime at = DateTime.Now;
            try
            {
                // Без UI ждать можно синхронно; Task.Run — чтобы не зависеть от контекста синхронизации Revit.
                Task.Run(async () =>
                {
                    foreach (var r in runs)
                        await attachments.SaveLogAsync(r.Number, r.Run.Log, creator).ConfigureAwait(false);

                    // Операция над свежей копией: результаты чужих ручных прогонов по другим проверкам сохраняются.
                    await store.UpdateAsync(DocumentKinds.AutoCheck, modelName, json =>
                    {
                        List<AutoCheckItem> items = AutoCheckStore.ParseRaw(json) ?? new List<AutoCheckItem>();
                        foreach (var r in runs)
                            AutoCheckStore.ApplyResult(AutoCheckStore.FindOrAdd(items, r.Number), r.Run, creator, at);
                        return AutoCheckStore.Serialize(items);
                    }).ConfigureAwait(false);
                }).GetAwaiter().GetResult();
            }
            catch (DocumentStoreUnavailableException ex)
            {
                throw new InvalidOperationException(
                    $"Сервер TNov недоступен — результаты автопроверок НЕ сохранены ({result.JsonPath}): {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Не удалось записать результаты автопроверок ({result.JsonPath}): {ex.Message}", ex);
            }

            return result;
        }

        /// <summary>
        /// Хранилище по TNovConfig.json; в файловом режиме — от переданной папки projects,
        /// как раньше (TNovAuto вычисляет её из того же ServerPath).
        /// </summary>
        private static IDocumentStore CreateStore(string projectsFolder)
        {
            TNovConfig config = TNovConfigLoad.GetCachedConfig();
            IDocumentStore store = config != null ? DocumentStores.ForChecklist(config) : null;
            if (store is ApiDocumentStore) return store;

            string serverPath = Path.GetDirectoryName(projectsFolder.TrimEnd('\\', '/'));
            return new FileDocumentStore(serverPath.EndsWith("\\") ? serverPath : serverPath + "\\");
        }
    }

    public sealed class AutoCheckBatchResult
    {
        /// <summary>Где лежит документ: путь к JSON в файловом режиме, «api:autocheck/{модель}» в режиме API.</summary>
        public string JsonPath { get; set; }
        public List<AutoCheckBatchItem> Items { get; } = new List<AutoCheckBatchItem>();
    }

    public sealed class AutoCheckBatchItem
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public bool Passed { get; set; }
        /// <summary>Текст исключения, если проверка упала; null — отработала штатно.</summary>
        public string Error { get; set; }
    }
}
