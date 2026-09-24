#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TNovApi.Client;

namespace TNovUtils.Checklist.Report
{
    /// <summary>Виды документов Чек-листа — как kind в TNovApi (TNovCommon.Storage.DocumentKinds).</summary>
    public static class ChecklistDocumentKinds
    {
        public const string AutoCheck = "autocheck";
        public const string BimCheck = "bimcheck";

        /// <summary>Суффикс файла на шаре: {model},{суффикс}.json.</summary>
        public static string FileSuffix(string kind) =>
            kind == BimCheck ? "BIM проверки" : kind;
    }

    /// <summary>
    /// Откуда отчёт берёт JSON Чек-листа (автопроверки, BIM-проверки).
    /// Журнал синхронизаций, roles.txt и CDE.txt по-прежнему читаются с шары.
    /// Файл общий с TNovDesktop — без Revit API и TNovCommon.
    /// </summary>
    public interface IChecklistDataSource
    {
        /// <summary>Для журнала и предупреждений: "files" / "api".</summary>
        string Name { get; }

        /// <summary>
        /// Вызывается один раз перед чтением: API здесь загружает документы всех моделей разом.
        /// <see cref="ChecklistSourceUnavailableException"/> — источник недоступен целиком.
        /// </summary>
        void Prepare(IReadOnlyCollection<string> models);

        /// <summary>JSON документа или null, если его нет. Исключение — ошибка чтения этого документа.</summary>
        string ReadJson(string kind, string model);
    }

    public sealed class ChecklistSourceUnavailableException : Exception
    {
        public ChecklistSourceUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Выбор источника по полям TNovConfig.json (ChecklistStorage, ApiUrl) — как DocumentStores.ForChecklist.</summary>
    public static class ChecklistDataSources
    {
        public const string StorageApi = "api";

        public static bool UsesApi(string checklistStorage, string apiUrl) =>
            string.Equals(checklistStorage, StorageApi, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiUrl);
    }

    /// <summary>Файлы {ServerPath}projects\{model},{суффикс}.json — прежнее поведение отчёта.</summary>
    public sealed class FileChecklistSource : IChecklistDataSource
    {
        private readonly string _projects;

        public FileChecklistSource(string serverPath)
        {
            _projects = Path.Combine(serverPath, "projects");
        }

        public string Name => "files";

        public void Prepare(IReadOnlyCollection<string> models) { }

        /// <summary>Путь — как в JsonDataService.GetJsonPath: {docName},{name}.json.</summary>
        public string ReadJson(string kind, string model)
        {
            string path = Path.Combine(_projects, $"{model},{ChecklistDocumentKinds.FileSuffix(kind)}.json");
            if (!File.Exists(path)) return null;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return File.ReadAllText(path);
                }
                catch (IOException) when (attempt < 2)
                {
                    // Файл может быть занят окном Чек-листа у пользователя — как в JsonDataService.
                    Thread.Sleep(300);
                }
            }
        }
    }

    /// <summary>
    /// Документы TNovApi (kind = autocheck / bimcheck, key = имя модели).
    /// Запросы: по одному списку на kind (только метаданные — заодно проверка доступности),
    /// затем GET лишь существующих документов нужных моделей, параллельно.
    /// </summary>
    public sealed class ApiChecklistSource : IChecklistDataSource
    {
        /// <summary>Сколько GET идут одновременно: хватает, чтобы спрятать RTT Хабаровск–Пермь.</summary>
        private const int Parallelism = 8;

        /// <summary>Максимум сервера для списка документов (DocumentEndpoints.MaxListLimit).</summary>
        private const int ListLimit = 5000;

        private static readonly string[] Kinds = { ChecklistDocumentKinds.AutoCheck, ChecklistDocumentKinds.BimCheck };

        private readonly Func<TNovApiClient> _client;
        private readonly Dictionary<string, string> _json = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _errors = new Dictionary<string, Exception>(StringComparer.Ordinal);

        /// <param name="client">Общий клиент процесса (keep-alive); создаётся лениво, ошибка адреса = недоступность.</param>
        public ApiChecklistSource(Func<TNovApiClient> client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public string Name => "api";

        /// <summary>Сколько HTTP-запросов сделал последний Prepare (для замеров).</summary>
        public int RequestCount { get; private set; }

        public void Prepare(IReadOnlyCollection<string> models)
        {
            _json.Clear();
            _errors.Clear();
            RequestCount = 0;
            if (models == null || models.Count == 0) return;

            TNovApiClient api;
            List<(string Kind, string Model)> wanted;
            try
            {
                api = _client();
                var lists = Kinds.Select(k => api.ListDocumentsAsync(k, limit: ListLimit)).ToArray();
                RequestCount += lists.Length;
                Task.WaitAll(lists);

                var needed = new HashSet<string>(models, StringComparer.Ordinal);
                wanted = new List<(string, string)>();
                for (int i = 0; i < Kinds.Length; i++)
                {
                    var list = lists[i].Result;
                    if (list.Count >= ListLimit)
                    {
                        // Список обрезан — не отсеиваем по нему, спрашиваем все модели (нет документа — 404 → null).
                        string kind = Kinds[i];
                        wanted.AddRange(needed.Select(m => (kind, m)));
                        continue;
                    }
                    foreach (var info in list)
                        if (needed.Contains(info.Key))
                            wanted.Add((Kinds[i], info.Key));
                }
            }
            catch (Exception ex)
            {
                throw new ChecklistSourceUnavailableException(Describe(Unwrap(ex)), Unwrap(ex));
            }

            RequestCount += wanted.Count;
            using (var gate = new SemaphoreSlim(Parallelism))
            {
                var tasks = wanted.Select(async w =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var doc = await api.GetDocumentAsync<JToken>(w.Kind, w.Model).ConfigureAwait(false);
                        // Без переформатирования дат: строки остаются как в файле,
                        // дальше JSON разбирается тем же JsonConvert, что и файл.
                        string json = doc?.Data == null ? null : JsonConvert.SerializeObject(doc.Data, Formatting.None); // без JToken.ToString(Formatting): нет в Newtonsoft Revit
                        lock (_json) _json[Id(w.Kind, w.Model)] = json;
                    }
                    catch (Exception ex)
                    {
                        lock (_json) _errors[Id(w.Kind, w.Model)] = Unwrap(ex);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToArray();
                Task.WaitAll(tasks);
            }

            // Если не прошёл ни один GET (сервер упал между списком и чтением) — это тоже недоступность.
            if (wanted.Count > 0 && _errors.Count == wanted.Count && _errors.Values.All(IsConnectivity))
            {
                var first = _errors.Values.First();
                throw new ChecklistSourceUnavailableException(Describe(first), first);
            }
        }

        public string ReadJson(string kind, string model)
        {
            string id = Id(kind, model);
            if (_errors.TryGetValue(id, out Exception error))
                throw new IOException(error.Message, error);
            return _json.TryGetValue(id, out string json) ? json : null;
        }

        private static string Id(string kind, string model) => kind + "\n" + model;

        private static bool IsConnectivity(Exception ex) =>
            ex is HttpRequestException || ex is TimeoutException;

        /// <summary>Сообщения клиента уже начинаются с «TNovApi», у сетевых ошибок префикса нет.</summary>
        private static string Describe(Exception ex) =>
            ex.Message.StartsWith("TNovApi", StringComparison.Ordinal) ? ex.Message : "TNovApi: " + ex.Message;

        private static Exception Unwrap(Exception ex)
        {
            if (ex is AggregateException agg)
                ex = agg.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
            return ex;
        }
    }
}
