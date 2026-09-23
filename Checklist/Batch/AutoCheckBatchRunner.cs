using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using TNovCommon;
using TNovCommon.Server;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.Batch
{
    /// <summary>
    /// Прогон автопроверок без UI (пакетный режим TNovAuto).
    /// Пишет тот же {modelName},autocheck.json и _checklogs, что и окно чек-листа.
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
        /// <param name="projectsFolder">{ServerPath}projects</param>
        public static AutoCheckBatchResult Run(Document doc, string modelName, string projectsFolder, string creator)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(modelName)) throw new ArgumentException("Пустое имя модели", nameof(modelName));
            if (string.IsNullOrWhiteSpace(projectsFolder)) throw new ArgumentException("Не задана папка projects", nameof(projectsFolder));

            ServerDirectories.Ensure(projectsFolder);
            string jsonPath = Path.Combine(projectsFolder, $"{modelName},autocheck.json");
            string logsFolder = AutoCheckStore.LogsFolderFor(jsonPath);
            ServerDirectories.Ensure(logsFolder);

            // Читаем напрямую: JsonDataService.LoadAuto требует RevitAPI.UiApplication.
            List<AutoCheckItem> items = File.Exists(jsonPath)
                ? JsonConvert.DeserializeObject<List<AutoCheckItem>>(File.ReadAllText(jsonPath)) ?? new List<AutoCheckItem>()
                : new List<AutoCheckItem>();

            var result = new AutoCheckBatchResult { JsonPath = jsonPath };

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

                var item = items.FirstOrDefault(i => i.Number == check.Number);
                if (item == null)
                {
                    item = new AutoCheckItem { Number = check.Number };
                    items.Add(item);
                }
                AutoCheckStore.ApplyResult(item, run, creator, logsFolder);

                result.Items.Add(new AutoCheckBatchItem
                {
                    Number = check.Number,
                    Title = run.Title ?? check.Title,
                    Passed = run.Passed,
                    Error = error
                });
            }

            JsonDataService.SaveAuto(jsonPath, items);
            return result;
        }
    }

    public sealed class AutoCheckBatchResult
    {
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
