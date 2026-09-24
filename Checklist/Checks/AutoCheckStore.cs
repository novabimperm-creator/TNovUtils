using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using TNovCommon;
using TNovCommon.Storage;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Общий JSON автопроверок с Журналом: {docName},autocheck.json (или документ autocheck в TNovApi).
    /// </summary>
    public sealed class AutoCheckStore : IDisposable
    {
        public const int GridsLevelsLinksNumber = Report.ChecklistCatalog.GridsLevelsLinksNumber;
        public const int AntiMirrorNumber = Report.ChecklistCatalog.AntiMirrorNumber;
        public const int RebarNoMarkNumber = Report.ChecklistCatalog.RebarNoMarkNumber;
        public const int NoPartsNumber = Report.ChecklistCatalog.NoPartsNumber;
        public const int LintelsNoMarkNumber = Report.ChecklistCatalog.LintelsNoMarkNumber;
        public const int EvacuationRoutesNumber = Report.ChecklistCatalog.EvacuationRoutesNumber;
        public const int UnplacedRoomsNumber = Report.ChecklistCatalog.UnplacedRoomsNumber;
        public const int RoomDepartmentNumber = Report.ChecklistCatalog.RoomDepartmentNumber;
        public const int AdskPostcheckNumber = Report.ChecklistCatalog.AdskPostcheckNumber;
        public const int RfCoordinationNumber = Report.ChecklistCatalog.RfCoordinationNumber;

        private static readonly JsonSerializerSettings SaveSettings = new JsonSerializerSettings { Formatting = Formatting.Indented };

        private readonly ChecklistSession _session;
        private readonly SharedDocument<List<AutoCheckItem>> _doc;
        private readonly IReadOnlyList<int> _baseNumbers;
        private readonly string _userName;
        private bool _busy;
        private bool _disposed;
        private List<AutoCheckItem> _items = new List<AutoCheckItem>();

        public string LogsRootFolder { get; }

        public ChecklistAttachments Attachments => _session.Attachments;

        /// <summary>Можно запускать проверки и сохранять результат (сервер доступен, данные загружены).</summary>
        public bool CanEdit => _doc.CanEdit;

        public event EventHandler Changed;

        public event EventHandler CanEditChanged;

        public AutoCheckStore(Document doc, ChecklistSession session)
        {
            _session = session;
            LogsRootFolder = session.Attachments.LogsRoot;

            // Имя пользователя и номера базовых проверок — здесь, в UI-потоке: слияние идёт и в фоне.
            _userName = RevitAPI.UiApplication?.Application?.Username ?? "";
            _baseNumbers = new BaseItems().numbers.ToList();

            _doc = new SharedDocument<List<AutoCheckItem>>(
                session, DocumentKinds.AutoCheck, "автопроверки", Dispatcher.CurrentDispatcher,
                Parse, Serialize, ApplyServer, () => _busy || _disposed);
            _doc.CanEditChanged += (s, e) => CanEditChanged?.Invoke(this, EventArgs.Empty);
            _doc.Start();
        }

        /// <summary>Внеочередная проверка сервера (без блокировки UI).</summary>
        public void CheckServerNow() => _session.CheckNow();

        public AutoCheckItem Get(int number) =>
            _items.FirstOrDefault(i => i.Number == number);

        public void ApplyRun(int number, CheckRunResult result, string userName) =>
            ApplyRuns(new[] { (number, result) }, userName);

        /// <summary>
        /// UI-поток: результат прогона сразу виден в окне; в фоне пишутся логи и тот же результат
        /// применяется к свежей копии документа (чужие результаты по другим проверкам сохраняются).
        /// </summary>
        public void ApplyRuns(IReadOnlyList<(int Number, CheckRunResult Result)> results, string userName)
        {
            if (!_doc.CanEdit)
            {
                new InfoWindow280(ChecklistSession.OfflineText + ".\nРезультат проверки не сохранён.").ShowDialog();
                return;
            }

            DateTime at = DateTime.Now;
            var runs = results.ToList();

            foreach (var r in runs)
                ApplyResult(GetOrCreate(r.Number), r.Result, userName, at);
            Changed?.Invoke(this, EventArgs.Empty);

            var attachments = _session.Attachments;
            _doc.Edit(
                items =>
                {
                    foreach (var r in runs)
                        ApplyResult(FindOrAdd(items, r.Number), r.Result, userName, at);
                    return true;
                },
                before: async () =>
                {
                    // Лог — раньше документа: кто увидит новый результат, сразу откроет и его лог.
                    foreach (var r in runs)
                        await attachments.SaveLogAsync(r.Number, r.Result.Log, userName).ConfigureAwait(false);
                });
        }

        /// <summary>Записывает результат прогона в пункт (лог пишется отдельно). Общее с AutoCheckBatchRunner.</summary>
        internal static void ApplyResult(AutoCheckItem item, CheckRunResult result, string userName, DateTime at)
        {
            item.Title = result.Title;
            item.IsChecked = result.Passed;
            item.ElemIds = result.ElemIds ?? "";
            item.Creator = userName;
            item.CreationDate = at;
        }

        internal static AutoCheckItem FindOrAdd(List<AutoCheckItem> items, int number)
        {
            var item = items.FirstOrDefault(i => i.Number == number);
            if (item != null) return item;
            item = new AutoCheckItem { Number = number };
            items.Add(item);
            return item;
        }

        /// <summary>Разбор документа как есть (null — документа нет). Без RevitAPI.</summary>
        internal static List<AutoCheckItem> ParseRaw(string json) =>
            string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<List<AutoCheckItem>>(json);

        /// <summary>Тот же формат, что у прежнего JsonDataService.SaveAuto.</summary>
        internal static string Serialize(List<AutoCheckItem> items) => JsonConvert.SerializeObject(items, SaveSettings);

        public void SetBusy(bool busy) => _busy = busy;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Dispose();
        }

        private AutoCheckItem GetOrCreate(int number)
        {
            var item = Get(number);
            if (item != null) return item;

            item = new AutoCheckItem { Number = number };
            item.SetLogsRootFolder(LogsRootFolder);
            _items.Add(item);
            return item;
        }

        /// <summary>
        /// Фон: разбор + то же слияние, что было в JsonDataService.LoadAuto (базовые пункты +
        /// ранее пройденные). Сохраняется тоже слитый список — как раньше SaveAuto(_items).
        /// Имя пользователя взято в UI-потоке, поэтому RevitAPI здесь не нужен.
        /// </summary>
        private List<AutoCheckItem> Parse(string json) => MergeWithBase(ParseRaw(json), _baseNumbers, DateTime.Now, _userName);

        private static List<AutoCheckItem> MergeWithBase(List<AutoCheckItem> current, IReadOnlyList<int> baseNumbers, DateTime now, string userName)
        {
            var result = new List<AutoCheckItem>();
            foreach (int number in baseNumbers)
            {
                result.Add(current?.FirstOrDefault(c => c.Number == number) ?? new AutoCheckItem
                {
                    Number = number,
                    IsChecked = false,
                    CreationDate = now,
                    Creator = userName
                });
            }

            // Проверки, которых нет в этой сборке (добавлены в более новой версии плагина или
            // отключены для раздела): сохраняем как есть в конце списка, иначе старая версия
            // при первом же сохранении стёрла бы чужие результаты. В окне они не показываются —
            // разделы окна берут пункты по номеру своей проверки (Get(number)).
            if (current != null)
            {
                var known = new HashSet<int>(baseNumbers);
                foreach (var item in current)
                {
                    if (item != null && known.Add(item.Number))
                        result.Add(item);
                }
            }
            return result;
        }

        /// <summary>UI-поток: документ с сервера (загрузка, опрос или итог своего сохранения).</summary>
        private void ApplyServer(StoredDocument stored, List<AutoCheckItem> server)
        {
            var snapshot = _items
                .Select(i => (i.Number, i.CreationDate, i.IsChecked, i.Title))
                .ToList();
            if (!HasMeaningfulChange(snapshot, server)) return;

            _items = server;
            foreach (var item in _items)
                item.SetLogsRootFolder(LogsRootFolder);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static bool HasMeaningfulChange(
            List<(int Number, DateTime CreationDate, bool IsChecked, string Title)> local,
            List<AutoCheckItem> server)
        {
            if (server == null) return false;
            if (local.Count != server.Count) return true;

            foreach (var remote in server)
            {
                var ours = local.FirstOrDefault(i => i.Number == remote.Number);
                if (ours.Number == 0 && remote.Number != 0)
                    return true;
                // Заготовка (проверку не запускали) при слиянии меняет только дату — не в счёт.
                if (string.IsNullOrWhiteSpace(ours.Title) && string.IsNullOrWhiteSpace(remote.Title))
                    continue;
                if (ours.CreationDate != remote.CreationDate ||
                    ours.IsChecked != remote.IsChecked ||
                    ours.Title != remote.Title)
                    return true;
            }
            return false;
        }
    }

    public sealed class CheckRunResult
    {
        public string Title { get; set; }
        public bool Passed { get; set; }
        public string ElemIds { get; set; }
        public string Log { get; set; }
    }
}
