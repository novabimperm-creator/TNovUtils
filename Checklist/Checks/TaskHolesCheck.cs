using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class TaskHolesCheck : ObservableObject, ICheck
    {
        public const string CheckId = "task-holes";
        public const string DisplayTitle = Report.ChecklistCatalog.TaskHolesTitle;
        public const string ResultTitle = "Расхождений отверстий с заданиями не найдено";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.TaskHolesNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public TaskHolesCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.TaskHolesNumber,
            DisplayTitle,
            ResultTitle,
            doc => TaskHolesChecker.Run(doc, allowUi: true));

        public CheckRunResult Run(Document doc) => TaskHolesChecker.Run(doc, allowUi: true);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.TaskHolesNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Отверстия и прочие задания (рамы, шахты, приямки) в группах-заданиях текущей модели должны
    /// совпадать с группами в связи задания — логика «Детального анализа» TNovTasks (TaskTools.HolesInGroup).
    /// Проверяются только группы, уже вставленные в модель. Сопоставление по Марке; расхождения размеров
    /// и координат — больше 20 мм. Согласование КР/BIM не проверяется. Модель не меняет
    /// (кроме открытия набора и загрузки связи задания).
    /// </summary>
    public static class TaskHolesChecker
    {
        private const double ToleranceMm = 20;
        private const double FeetToMm = 304.8;
        private const string PrivateLinkMarker = "Не общедоступное";

        private const string HoleFamily = "pmN.Отверстие";
        private static readonly string[] OtherFamilies = { "pmN.Рама под оборудование", "pmN.Задание на шахту", "pmN.Задание на приямок" };

        private static readonly Guid HoleWidthGuid = new Guid("096bc30e-3c95-4637-84d5-9f6bf45d8676");  // ADSK_Отверстие_Ширина
        private static readonly Guid HoleHeightGuid = new Guid("bc4e92d8-db66-4e93-8923-3af6e2dc8599"); // ADSK_Отверстие_Высота
        private static readonly Guid DiameterGuid = new Guid("9b679ab7-ea2e-49ce-90ab-0549d5aa36ff");   // ADSK_Размер_Диаметр
        private static readonly Guid WidthGuid = new Guid("8f2e4f93-9472-4941-a65d-0ac468fd6a5d");      // ADSK_Размер_Ширина
        private static readonly Guid HeightGuid = new Guid("da753fe3-ecfa-465b-9a2c-02f55d0c2ff1");     // ADSK_Размер_Высота
        private static readonly Guid LengthGuid = new Guid("748a2515-4cc9-4b74-9a69-339a8d65a212");     // ADSK_Размер_Длина

        public static CheckRunResult Run(Document doc, bool allowUi)
        {
            var log = new StringBuilder();
            var reports = new List<GroupReport>();
            var skipped = new List<string>();

            var links = LinkLoading.CollectLoaded(doc, Report.ChecklistCatalog.TaskLinkMarkers, "заданий", allowUi, log,
                loadUnloaded: true, exclude: PrivateLinkMarker);
            if (links.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("Загруженные связи заданий не найдены — проверять нечего");
                return Result(reports, skipped, log);
            }

            // Группы текущей модели по короткому имени; при нескольких экземплярах — первый, как в TNovTasks
            var hostGroups = new Dictionary<string, Group>(StringComparer.Ordinal);
            foreach (var g in CollectGroups(doc))
            {
                string key = ShortName(g.Name);
                if (!hostGroups.ContainsKey(key)) hostGroups[key] = g;
            }

            var outside = new OutsideIndex(doc);
            var processed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in links)
            {
                foreach (var linkGroup in CollectGroups(link.Document))
                {
                    string name = ShortName(linkGroup.Name);
                    if (!processed.Add(name)) continue;
                    if (!hostGroups.TryGetValue(name, out var hostGroup))
                    {
                        skipped.Add(name);
                        continue;
                    }
                    try
                    {
                        reports.Add(CompareGroup(name, link, linkGroup, hostGroup, outside));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Чек-лист: ошибка сравнения группы задания {name}: {ex}", 3);
                        reports.Add(new GroupReport { Name = name, LinkName = link.Name, Error = ex.Message });
                    }
                }
            }

            return Result(reports, skipped, log);
        }

        private static CheckRunResult Result(List<GroupReport> reports, List<string> skipped, StringBuilder log)
        {
            var bad = reports.Where(r => r.Error != null || r.Problems.Count > 0).ToList();
            var hostIds = new List<string>();

            if (bad.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Группы заданий с расхождениями: {bad.Count}, элементов: {bad.Sum(r => r.Problems.Count)}");
                foreach (var r in bad.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    log.AppendLine();
                    if (r.Error != null)
                    {
                        log.AppendLine($"Группа {r.Name} (связь {r.LinkName}) — ошибка проверки: {r.Error}");
                        continue;
                    }
                    log.AppendLine($"Группа {r.Name} (связь {r.LinkName}) — расхождений: {r.Problems.Count}");
                    foreach (var p in r.Problems.OrderBy(p => p.Order).ThenBy(p => p.Mark, MarkComparer.Instance))
                    {
                        string id = p.HostId != null ? " | Id " + ElementIds.ToStringValue(p.HostId) : "";
                        log.AppendLine($"  {p.Kind} {p.Mark} | {p.Size} | {p.Position} | {string.Join(" ", p.Statuses)}{id}");
                    }
                    var ids = r.Problems.Where(p => p.HostId != null).Select(p => ElementIds.ToStringValue(p.HostId)).Distinct().ToList();
                    if (ids.Count > 0)
                    {
                        log.AppendLine("Id: " + string.Join(", ", ids));
                        hostIds.AddRange(ids);
                    }
                }
            }

            var ok = reports.Where(r => r.Error == null && r.Problems.Count == 0).Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (ok.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Актуальные группы ({ok.Count}): {string.Join(", ", ok)}");
            }
            if (skipped.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Пропущены — ещё не вставлялись в модель ({skipped.Count}): {string.Join(", ", skipped.OrderBy(n => n, StringComparer.Ordinal))}");
            }
            if (reports.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("В модели нет вставленных групп заданий");
            }

            hostIds = hostIds.Distinct().ToList();
            return new CheckRunResult
            {
                Title = TaskHolesCheck.ResultTitle,
                Passed = bad.Count == 0,
                ElemIds = hostIds.Count > 0 ? string.Join(", ", hostIds) : "",
                Log = log.ToString()
            };
        }

        // ---- Сравнение группы ----

        private static GroupReport CompareGroup(string name, LoadedLink link, Group linkGroup, Group hostGroup, OutsideIndex outside)
        {
            var report = new GroupReport { Name = name, LinkName = link.Name };
            var linkHoles = Members(linkGroup, IsHole);
            var linkOthers = Members(linkGroup, IsOther);
            var hostHoles = Members(hostGroup, IsHole);
            var hostOthers = Members(hostGroup, IsOther);

            CompareKind(report, link, linkHoles, hostHoles, outside.Holes, isHole: true);
            CompareKind(report, link, linkOthers, hostOthers, outside.Others, isHole: false);
            return report;
        }

        private static void CompareKind(GroupReport report, LoadedLink link, List<Element> linkElems, List<Element> hostElems,
            Dictionary<string, Element> outside, bool isHole)
        {
            string kind = isHole ? "Отверстие" : "Задание";
            var extras = new List<Element>(hostElems);
            var hostByMark = new Dictionary<string, Element>(StringComparer.Ordinal);
            foreach (var e in hostElems)
            {
                string mark = Mark(e);
                if (!string.IsNullOrEmpty(mark) && !hostByMark.ContainsKey(mark)) hostByMark[mark] = e;
            }

            foreach (var linkElem in linkElems)
            {
                string mark = Mark(linkElem);
                if (string.IsNullOrEmpty(mark)) continue; // элементы задания без Марки пропускаются, как в TNovTasks

                var task = Dims(linkElem, isHole);
                XYZ taskPoint = Point(linkElem);
                if (taskPoint != null) taskPoint = link.Transform.OfPoint(taskPoint);

                var problem = new Problem
                {
                    Kind = kind,
                    Mark = mark,
                    Size = task.Text,
                    Position = PositionText(taskPoint)
                };

                bool outsideGroup = false;
                if (!hostByMark.TryGetValue(mark, out var host))
                {
                    if (outside.TryGetValue(mark, out host)) outsideGroup = true;
                }

                if (host == null)
                {
                    problem.Statuses.Add("Не вставлено.");
                    problem.Order = 1;
                    report.Problems.Add(problem);
                    continue;
                }

                extras.Remove(host);
                problem.HostId = host.Id;
                problem.Order = 3;

                var actual = Dims(host, isHole);
                if (isHole && task.Circle != actual.Circle)
                {
                    problem.Statuses.Add(task.Circle ? "Отверстие в задании изменено на круглое." : "Отверстие в задании изменено на прямоугольное.");
                }
                else
                {
                    foreach (var (label, taskValue, actualValue) in task.Values.Zip(actual.Values, (t, a) => (t.Label, t.Value, a.Value)))
                    {
                        if (taskValue.HasValue && actualValue.HasValue && Math.Abs(taskValue.Value - actualValue.Value) > ToleranceMm + 1e-6)
                            problem.Statuses.Add($"{label}: {actualValue.Value:0} (в задании {taskValue.Value:0}).");
                    }
                }

                XYZ hostPoint = Point(host);
                if (taskPoint != null && hostPoint != null)
                {
                    AddCoord(problem, "X", taskPoint.X, hostPoint.X);
                    AddCoord(problem, "Y", taskPoint.Y, hostPoint.Y);
                    AddCoord(problem, "Z", taskPoint.Z, hostPoint.Z);
                }

                if (outsideGroup) problem.Statuses.Add("Вставлено вне группы.");
                if (problem.Statuses.Count > 0) report.Problems.Add(problem);
            }

            foreach (var e in extras)
            {
                string mark = Mark(e);
                if (string.IsNullOrEmpty(mark)) mark = "-";
                report.Problems.Add(new Problem
                {
                    Kind = kind,
                    Mark = mark,
                    Size = Dims(e, isHole).Text,
                    Position = PositionText(Point(e)),
                    HostId = e.Id,
                    Order = 2,
                    Statuses = { isHole ? "Лишнее отверстие — удалено в Задании." : "Лишний элемент — удалён в Задании." }
                });
            }
        }

        private static void AddCoord(Problem problem, string axis, double taskFeet, double actualFeet)
        {
            double task = taskFeet * FeetToMm, actual = actualFeet * FeetToMm;
            if (Math.Abs(task - actual) > ToleranceMm + 1e-6)
                problem.Statuses.Add($"{axis}: {actual / 1000:0.000} (в задании {task / 1000:0.000}).");
        }

        // ---- Элементы ----

        private static List<Group> CollectGroups(Document doc) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_IOSModelGroups)
                .WhereElementIsNotElementType()
                .OfType<Group>()
                .ToList();

        /// <summary>Короткое имя группы — первые три части через «_» (учёт групп по старой концепции), как в TNovTasks.</summary>
        private static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string[] parts = name.Split('_');
            return parts.Length > 2 ? parts[0] + "_" + parts[1] + "_" + parts[2] : name;
        }

        private static List<Element> Members(Group group, Func<FamilyInstance, bool> predicate)
        {
            var doc = group.Document;
            return group.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance)))
                .Select(doc.GetElement)
                .OfType<FamilyInstance>()
                .Where(predicate)
                .Cast<Element>()
                .ToList();
        }

        private static string FamilyName(FamilyInstance fi) => fi.Symbol?.FamilyName ?? "";

        private static bool IsHole(FamilyInstance fi) => FamilyName(fi).Contains(HoleFamily);

        private static bool IsOther(FamilyInstance fi)
        {
            string family = FamilyName(fi);
            return OtherFamilies.Any(family.Contains);
        }

        private static string Mark(Element e)
        {
            var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            return p?.AsString() ?? p?.AsValueString();
        }

        private static XYZ Point(Element e) => (e.Location as LocationPoint)?.Point;

        private static double? Mm(Element e, Guid guid)
        {
            var p = e.get_Parameter(guid);
            if (p == null || p.StorageType != StorageType.Double) return null;
            return p.AsDouble() * FeetToMm;
        }

        private static Dimensions Dims(Element e, bool isHole)
        {
            var d = new Dimensions();
            if (isHole)
            {
                d.Circle = e.get_Parameter(DiameterGuid) != null;
                if (d.Circle)
                {
                    d.Values.Add(("Диаметр", Mm(e, DiameterGuid)));
                }
                else
                {
                    d.Values.Add(("Ширина", Mm(e, HoleWidthGuid)));
                    d.Values.Add(("Высота", Mm(e, HoleHeightGuid)));
                }
            }
            else
            {
                d.Values.Add(("Длина", Mm(e, LengthGuid)));
                d.Values.Add(("Ширина", Mm(e, WidthGuid)));
                d.Values.Add(("Высота", Mm(e, HeightGuid)));
            }
            return d;
        }

        private static string PositionText(XYZ p) =>
            p == null ? "координаты —" : $"X {p.X * FeetToMm / 1000:0.000} Y {p.Y * FeetToMm / 1000:0.000} Z {p.Z * FeetToMm / 1000:0.000}";

        // ---- Типы ----

        private sealed class Dimensions
        {
            public bool Circle;
            public List<(string Label, double? Value)> Values = new List<(string Label, double? Value)>();

            public string Text => Circle
                ? "Ø" + Format(Values[0].Value)
                : string.Join("×", Values.Select(v => Format(v.Value)));

            private static string Format(double? v) => v.HasValue ? v.Value.ToString("0") : "?";
        }

        private sealed class Problem
        {
            public string Kind;
            public string Mark;
            public string Size;
            public string Position;
            public ElementId HostId;
            public int Order; // как holeorder в TNovTasks: не вставлено, лишнее, расхождения
            public List<string> Statuses = new List<string>();
        }

        private sealed class GroupReport
        {
            public string Name;
            public string LinkName;
            public string Error;
            public List<Problem> Problems = new List<Problem>();
        }

        /// <summary>
        /// Элементы-задания текущей модели по Марке для поиска вставленных вне группы (как в TNovTasks:
        /// Обобщённые модели с «Отверстие» / «Рама под оборудование» в параметре типа Группа модели).
        /// </summary>
        private sealed class OutsideIndex
        {
            public readonly Dictionary<string, Element> Holes = new Dictionary<string, Element>(StringComparer.Ordinal);
            public readonly Dictionary<string, Element> Others = new Dictionary<string, Element>(StringComparer.Ordinal);

            public OutsideIndex(Document doc)
            {
                var instances = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>();
                foreach (var fi in instances)
                {
                    string mark = Mark(fi);
                    if (string.IsNullOrEmpty(mark)) continue;
                    string model = fi.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)?.AsString() ?? "";
                    if (model.Contains("Отверстие") || IsHole(fi))
                    {
                        if (!Holes.ContainsKey(mark)) Holes[mark] = fi;
                    }
                    else if (model.Contains("Рама под оборудование") || IsOther(fi))
                    {
                        if (!Others.ContainsKey(mark)) Others[mark] = fi;
                    }
                }
            }
        }

        /// <summary>Марки по числу, если обе числовые, иначе по строке.</summary>
        private sealed class MarkComparer : IComparer<string>
        {
            public static readonly MarkComparer Instance = new MarkComparer();

            public int Compare(string x, string y)
            {
                if (long.TryParse(x, out long a) && long.TryParse(y, out long b)) return a.CompareTo(b);
                return string.CompareOrdinal(x, y);
            }
        }
    }
}
