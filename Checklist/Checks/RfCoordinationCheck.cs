using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class RfCoordinationCheck : ObservableObject, ICheck
    {
        public const string CheckId = "rf-coordination";
        public const string DisplayTitle = Report.ChecklistCatalog.RfCoordinationTitle;
        public const string ResultTitle = "Проблем координации с РФ нет";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.RfCoordinationNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public RfCoordinationCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.RfCoordinationNumber,
            DisplayTitle,
            ResultTitle,
            doc => RfCoordinationChecker.Run(doc, allowUi: true));

        public CheckRunResult Run(Document doc) => RfCoordinationChecker.Run(doc, allowUi: true);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.RfCoordinationNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Координация с разбивочным файлом (_РФ / -РФ): связь есть, её набор «Общие уровни/слои и сетки» открыт,
    /// связь загружена, отслеживаемые оси и уровни совпадают с РФ.
    /// «Просмотр координаций» через API недоступен — расхождения мониторинга ищем сами.
    /// Открыть закрытый набор API не умеет; обход — ShowElements по элементу набора (только с UI).
    /// </summary>
    public static class RfCoordinationChecker
    {
        private const double Tolerance = 1 / 304.8; // 1 мм

        public static CheckRunResult Run(Document doc, bool allowUi)
        {
            var log = new List<string>();
            var failIds = new List<string>();
            bool fatal = false;

            var rfTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .Where(t => IsRf(t.Name))
                .ToList();

            if (rfTypes.Count == 0)
            {
                log.Add("\nСвязь РФ (_РФ / -РФ) не найдена\n");
                return Result(false, failIds, log);
            }

            var rfTypeIds = new HashSet<ElementId>(rfTypes.Select(t => t.Id));
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(i => rfTypeIds.Contains(i.GetTypeId()))
                .ToList();

            if (instances.Count == 0)
            {
                log.Add("\nЭкземпляр связи РФ не размещён в модели: " + string.Join(", ", rfTypes.Select(t => t.Name)) + "\n");
                return Result(false, failIds, log);
            }

            if (doc.IsWorkshared)
                fatal |= CheckWorksets(doc, instances, allowUi, log, failIds);

            if (!fatal)
                fatal |= EnsureLoaded(doc, rfTypes, log, failIds);

            if (!fatal)
                CheckMonitoring(doc, rfTypeIds, log, failIds);

            return Result(failIds.Count == 0 && !fatal, failIds, log);
        }

        private static CheckRunResult Result(bool passed, List<string> failIds, List<string> log)
        {
            var ids = failIds.Distinct().ToList();
            return new CheckRunResult
            {
                Title = RfCoordinationCheck.ResultTitle,
                Passed = passed && ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = string.Join("", log)
            };
        }

        /// <returns>true — набор закрыт и открыть не удалось, дальше проверять нечего.</returns>
        private static bool CheckWorksets(Document doc, List<RevitLinkInstance> instances, bool allowUi,
            List<string> log, List<string> failIds)
        {
            var table = doc.GetWorksetTable();
            var wrong = new List<RevitLinkInstance>();
            var stillClosed = new List<RevitLinkInstance>();
            var opened = new List<RevitLinkInstance>();

            foreach (var inst in instances)
            {
                var workset = table.GetWorkset(inst.WorksetId);
                if (workset == null) continue;
                if (!IsRfWorkset(workset.Name)) wrong.Add(inst);
                if (workset.IsOpen) continue;

                if (allowUi && TryOpenWorkset(doc, inst) && table.GetWorkset(inst.WorksetId).IsOpen)
                    opened.Add(inst);
                else
                    stillClosed.Add(inst);
            }

            AppendFinding("РФ не в наборе «Общие уровни и сетки»", wrong, log, failIds);
            if (opened.Count > 0)
                log.Add("\nРабочий набор связи РФ был закрыт и открыт автоматически: "
                        + string.Join(", ", opened.Select(i => WorksetName(table, i)).Distinct()) + "\n");
            if (stillClosed.Count > 0)
            {
                string names = string.Join(", ", stillClosed.Select(i => WorksetName(table, i)).Distinct());
                log.Add(allowUi
                    ? $"\nНе удалось открыть рабочий набор связи РФ: {names}. Откройте его вручную\n"
                    : $"\nРабочий набор связи РФ закрыт: {names}. Откройте его вручную\n");
                failIds.AddRange(stillClosed.Select(i => ElementIds.ToStringValue(i.Id)));
            }
            return stillClosed.Count > 0;
        }

        private static bool TryOpenWorkset(Document doc, Element element)
        {
            try
            {
                new UIDocument(doc).ShowElements(element.Id);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("РФ: не удалось открыть набор через ShowElements: " + ex.Message, 3);
                return false;
            }
        }

        private static string WorksetName(WorksetTable table, Element e) => table.GetWorkset(e.WorksetId)?.Name ?? "?";

        /// <returns>true — связь не загружена и загрузить не удалось.</returns>
        private static bool EnsureLoaded(Document doc, List<RevitLinkType> rfTypes, List<string> log, List<string> failIds)
        {
            bool fatal = false;
            foreach (var type in rfTypes)
            {
                if (RevitLinkType.IsLoaded(doc, type.Id)) continue;
                string error;
                try
                {
                    var result = type.Load().LoadResult;
                    error = result == LinkLoadResultType.LinkLoaded || result == LinkLoadResultType.UsedExisting
                        ? null
                        : "Revit вернул статус «" + result + "»";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (error == null)
                {
                    log.Add($"\nСвязь {type.Name} была выгружена и загружена автоматически\n");
                    continue;
                }
                log.Add($"\nНе удалось загрузить связь {type.Name}: {error}\n");
                failIds.Add(ElementIds.ToStringValue(type.Id));
                fatal = true;
            }
            return fatal;
        }

        private static void CheckMonitoring(Document doc, HashSet<ElementId> rfTypeIds, List<string> log, List<string> failIds)
        {
            var deleted = new List<Element>();
            var moved = new List<Element>();
            var renamed = new List<Element>();
            int monitored = 0;
            var cache = new Dictionary<(ElementId, bool), List<Element>>();

            var elements = new FilteredElementCollector(doc)
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Grids),
                    new ElementCategoryFilter(BuiltInCategory.OST_Levels)))
                .WhereElementIsNotElementType()
                .Where(e => e is Autodesk.Revit.DB.Grid || e is Level)
                .ToList();

            foreach (var element in elements)
            {
                if (!element.IsMonitoringLinkElement()) continue;
                // API отдаёт только экземпляры связи, а не исходный элемент в ней.
                // Исходник ищем по имени, иначе по положению (тогда он переименован).
                foreach (var instId in element.GetMonitoredLinkElementIds())
                {
                    if (!(doc.GetElement(instId) is RevitLinkInstance inst)) continue;
                    if (!rfTypeIds.Contains(inst.GetTypeId())) continue;
                    monitored++;

                    var linkDoc = inst.GetLinkDocument();
                    if (linkDoc == null) continue;
                    var transform = inst.GetTotalTransform();
                    var key = (inst.Id, element is Level);
                    if (!cache.TryGetValue(key, out var candidates))
                        cache[key] = candidates = SourceCandidates(linkDoc, element);

                    var byName = candidates.FirstOrDefault(c => string.Equals(c.Name, element.Name, StringComparison.Ordinal));
                    if (byName != null)
                    {
                        if (!SamePosition(element, byName, transform)) moved.Add(element);
                    }
                    else if (candidates.Any(c => SamePosition(element, c, transform)))
                        renamed.Add(element);
                    else
                        deleted.Add(element);
                }
            }

            if (monitored == 0)
                log.Add("\nОси и уровни модели не отслеживают связь РФ (мониторинг не настроен)\n");

            AppendFinding("Нет соответствия в РФ (удалён или изменён и переименован)", deleted, log, failIds);
            AppendFinding("Положение отличается от РФ", moved, log, failIds);
            AppendFinding("Имя отличается от РФ", renamed, log, failIds);
        }

        private static List<Element> SourceCandidates(Document linkDoc, Element host)
        {
            var category = host is Level ? BuiltInCategory.OST_Levels : BuiltInCategory.OST_Grids;
            return new FilteredElementCollector(linkDoc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();
        }

        private static bool SamePosition(Element host, Element source, Transform transform)
        {
            if (host is Level hostLevel && source is Level sourceLevel)
            {
                double z = transform.OfPoint(new XYZ(0, 0, sourceLevel.Elevation)).Z;
                return Math.Abs(hostLevel.Elevation - z) <= Tolerance;
            }

            if (host is Autodesk.Revit.DB.Grid hostGrid && source is Autodesk.Revit.DB.Grid sourceGrid)
            {
                var a = hostGrid.Curve;
                var b = sourceGrid.Curve?.CreateTransformed(transform);
                if (a == null || b == null) return true;

                // Длину и концы оси (подрезку) в модели меняют — сравниваем саму линию/окружность.
                if (a is Line la && b is Line lb)
                {
                    var da = Flat(la.Direction).Normalize();
                    var db = Flat(lb.Direction).Normalize();
                    if (Math.Abs(Math.Abs(da.DotProduct(db)) - 1) > 1e-9) return false;
                    var offset = Flat(la.GetEndPoint(0) - lb.GetEndPoint(0));
                    return Math.Abs(offset.CrossProduct(db).Z) <= Tolerance;
                }
                if (a is Arc aa && b is Arc ab)
                {
                    return Flat(aa.Center - ab.Center).GetLength() <= Tolerance
                           && Math.Abs(aa.Radius - ab.Radius) <= Tolerance;
                }
                return false;
            }

            return true;
        }

        private static XYZ Flat(XYZ v) => new XYZ(v.X, v.Y, 0);

        private static bool IsRf(string name) =>
            !string.IsNullOrEmpty(name) && (name.Contains("_РФ") || name.Contains("-РФ"));

        private static bool IsRfWorkset(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return (n.Contains("уровни") || n.Contains("слои")) && n.Contains("сетки");
        }

        private static void AppendFinding(string header, List<RevitLinkInstance> elements, List<string> log, List<string> ids) =>
            AppendFinding(header, elements.Cast<Element>().ToList(), log, ids);

        private static void AppendFinding(string header, List<Element> elements, List<string> log, List<string> ids)
        {
            if (elements.Count == 0) return;
            var distinct = elements.GroupBy(e => e.Id).Select(g => g.First()).ToList();
            var localIds = distinct.Select(e => ElementIds.ToStringValue(e.Id)).ToList();
            log.Add($"\n{header}: {string.Join(", ", distinct.Select(e => e.Name))}\nId: {string.Join(", ", localIds)}\n");
            ids.AddRange(localIds);
        }
    }
}
