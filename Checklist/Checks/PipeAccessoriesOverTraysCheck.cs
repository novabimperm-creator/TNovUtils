using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class PipeAccessoriesOverTraysCheck : ObservableObject, ICheck
    {
        public const string CheckId = "pipe-accessories-over-trays";
        public const string DisplayTitle = Report.ChecklistCatalog.PipeAccessoriesOverTraysTitle;
        public const string ResultTitle = "Арматура и заглушки труб над лотками не найдены";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.PipeAccessoriesOverTraysNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public PipeAccessoriesOverTraysCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.PipeAccessoriesOverTraysNumber,
            DisplayTitle,
            ResultTitle,
            doc => PipeAccessoriesOverTraysChecker.Run(doc, allowUi: true));

        public CheckRunResult Run(Document doc) => PipeAccessoriesOverTraysChecker.Run(doc, allowUi: true);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.PipeAccessoriesOverTraysNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Арматура труб и заглушки (фитинги с типом детали «Заглушка») модели ВК/ОВ не должны стоять
    /// над кабельными лотками связей ЭЛ/СС/ПС. Ошибка — если в плане элемент перекрывает лоток
    /// (или соединитель лотка), он выше лотка и зазор по высоте не больше 1500 мм.
    /// Проверяются только элементы на уровнях с кодом -1, -01, 00, 01, 1 в начале имени.
    /// Выгруженные связи и отсутствие лотков только отмечаются в логе. Модель не меняет.
    /// </summary>
    public static class PipeAccessoriesOverTraysChecker
    {
        private const double MaxGap = 1500 / 304.8;
        private const double CellSize = 10.0; // ~3 м, ячейка сетки поиска лотков в плане
        private const double LevelTolerance = 1 / 304.8;

        // Код уровня в начале имени: «01 +0.000 Этаж 1», «-1_Подвал»; «10…», «-10…», «001…» не подходят
        private static readonly Regex LevelCode = new Regex(@"^\s*(-01|-1|00|01|1)(?!\d)", RegexOptions.CultureInvariant);

        public static CheckRunResult Run(Document doc, bool allowUi)
        {
            var log = new StringBuilder();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
            if (!levels.Any(l => IsCheckedLevel(l.Name)))
            {
                log.AppendLine();
                log.AppendLine("В модели нет уровней с кодом -1, -01, 00, 01, 1 в начале имени — проверять нечего");
                return Result(new List<Finding>(), log);
            }

            var elements = CollectHostElements(doc, levels);
            if (elements.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("На проверяемых уровнях нет арматуры труб и заглушек");
                return Result(new List<Finding>(), log);
            }

            double zMin = elements.Min(e => e.Bottom) - MaxGap;
            double zMax = elements.Max(e => e.CenterZ);
            var trays = CollectTrays(doc, allowUi, zMin, zMax, log);
            if (trays.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("Лотки в связях ЭЛ/СС/ПС на проверяемых уровнях не найдены");
                return Result(new List<Finding>(), log);
            }

            var grid = new TrayGrid(trays);
            var findings = new List<Finding>();
            foreach (var e in elements)
            {
                Finding best = null;
                foreach (var tray in grid.Near(e.MinX, e.MinY, e.MaxX, e.MaxY))
                {
                    if (e.CenterZ <= tray.Top) continue;
                    double gap = e.Bottom - tray.Top;
                    if (gap > MaxGap) continue;
                    if (!tray.OverlapsInPlan(e.MinX, e.MinY, e.MaxX, e.MaxY)) continue;
                    if (best == null || gap < best.Gap)
                        best = new Finding { Element = e, Tray = tray, Gap = gap };
                }
                if (best != null) findings.Add(best);
            }

            return Result(findings, log);
        }

        private static CheckRunResult Result(List<Finding> findings, StringBuilder log)
        {
            if (findings.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Над кабельными лотками (зазор до {MaxGap * 304.8:0} мм) найдено элементов: {findings.Count}");
                var groups = findings
                    .GroupBy(f => new { f.Element.LevelName, f.Element.Category })
                    .OrderBy(g => g.Key.LevelName, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Category, StringComparer.Ordinal);
                foreach (var g in groups)
                {
                    log.AppendLine();
                    log.AppendLine($"{g.Key.LevelName} | {g.Key.Category} — {g.Count()} шт.");
                    foreach (var f in g.OrderBy(f => f.Element.Name, StringComparer.Ordinal))
                    {
                        log.AppendLine($"  {ElementIds.ToStringValue(f.Element.Id)} {f.Element.Name} — над лотком " +
                                       $"{f.Tray.LinkName} Id {ElementIds.ToStringValue(f.Tray.Id)}, зазор {Math.Max(0, f.Gap * 304.8):0} мм");
                    }
                    log.AppendLine("Id: " + string.Join(", ", g.Select(f => ElementIds.ToStringValue(f.Element.Id))));
                }
            }

            var ids = findings.Select(f => ElementIds.ToStringValue(f.Element.Id)).Distinct().ToList();
            return new CheckRunResult
            {
                Title = PipeAccessoriesOverTraysCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log.ToString()
            };
        }

        internal static bool IsCheckedLevel(string name) => !string.IsNullOrEmpty(name) && LevelCode.IsMatch(name);

        // ---- Элементы модели ----

        private static List<HostElement> CollectHostElements(Document doc, List<Level> levels)
        {
            var result = new List<HostElement>();
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_PipeAccessory),
                    new ElementCategoryFilter(BuiltInCategory.OST_PipeFitting)))
                .Cast<FamilyInstance>();

            foreach (var fi in instances)
            {
                if (fi.SuperComponent != null) continue;
                bool isFitting = fi.Category != null && fi.Category.Id.Equals(new ElementId(BuiltInCategory.OST_PipeFitting));
                if (isFitting && !IsCap(fi)) continue;

                var bb = fi.get_BoundingBox(null);
                if (bb == null) continue;
                double centerZ = (bb.Min.Z + bb.Max.Z) / 2;
                var level = LevelAt(levels, centerZ);
                if (level == null || !IsCheckedLevel(level.Name)) continue;

                result.Add(new HostElement
                {
                    Id = fi.Id,
                    Name = $"{fi.Symbol?.FamilyName}: {fi.Name}",
                    Category = isFitting ? "Заглушки" : fi.Category?.Name ?? "Арматура труб",
                    LevelName = level.Name,
                    MinX = bb.Min.X, MinY = bb.Min.Y, MaxX = bb.Max.X, MaxY = bb.Max.Y,
                    Bottom = bb.Min.Z,
                    CenterZ = centerZ
                });
            }
            return result;
        }

        private static bool IsCap(FamilyInstance fi)
        {
            try
            {
                return fi.MEPModel is MechanicalFitting fitting && fitting.PartType == PartType.Cap;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Уровень, в диапазоне которого точка: от его отметки до отметки следующего.
        /// Ниже нижнего уровня — нижний. Параметр уровня не берём: у арматуры на трубе он часто пуст.
        /// </summary>
        private static Level LevelAt(List<Level> sortedLevels, double z)
        {
            Level result = null;
            foreach (var level in sortedLevels)
            {
                if (level.Elevation <= z + LevelTolerance) result = level;
                else break;
            }
            return result ?? sortedLevels.FirstOrDefault();
        }

        // ---- Лотки из связей ----

        private static List<TrayShape> CollectTrays(Document doc, bool allowUi, double zMin, double zMax, StringBuilder log)
        {
            var trays = new List<TrayShape>();
            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .Where(t => !t.IsNestedLink && ModelNameRules.ContainsAny(t.Name, Report.ChecklistCatalog.ElSsPsMarkers))
                .ToList();
            if (linkTypes.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("Связи ЭЛ/СС/ПС (-ЭЛ, _ЭЛ, -СС, _СС, -ПС, _ПС) не найдены");
                return trays;
            }

            var allInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();
            var table = doc.IsWorkshared ? doc.GetWorksetTable() : null;

            foreach (var type in linkTypes)
            {
                var instances = allInstances.Where(i => i.GetTypeId() == type.Id).ToList();
                if (instances.Count == 0)
                {
                    log.AppendLine();
                    log.AppendLine($"Экземпляр связи {type.Name} не размещён в модели");
                    continue;
                }

                bool openedWorkset = false;
                if (table != null)
                {
                    foreach (var inst in instances)
                    {
                        var workset = table.GetWorkset(inst.WorksetId);
                        if (workset == null || workset.IsOpen) continue;
                        if (allowUi && LinkLoading.TryOpenWorkset(doc, inst) && table.GetWorkset(inst.WorksetId).IsOpen)
                        {
                            openedWorkset = true;
                            log.AppendLine();
                            log.AppendLine($"Рабочий набор «{workset.Name}» связи {type.Name} был закрыт и открыт автоматически");
                        }
                        else
                        {
                            log.AppendLine();
                            log.AppendLine($"Рабочий набор «{workset.Name}» связи {type.Name} закрыт — связь не проверена, откройте набор");
                        }
                    }
                }

                // Выгруженную пользователем связь не загружаем; загружаем только после открытия её набора
                if (!RevitLinkType.IsLoaded(doc, type.Id))
                {
                    string error = openedWorkset ? LinkLoading.TryLoad(doc, type) : "связь выгружена";
                    if (error != null)
                    {
                        log.AppendLine();
                        log.AppendLine($"Связь {type.Name} не проверена: {error}");
                        continue;
                    }
                }

                foreach (var inst in instances)
                {
                    var linkDoc = inst.GetLinkDocument();
                    if (linkDoc == null) continue;
                    CollectTrays(linkDoc, inst.GetTotalTransform(), type.Name, zMin, zMax, trays);
                }
            }
            return trays;
        }

        private static void CollectTrays(Document linkDoc, Transform transform, string linkName,
            double zMin, double zMax, List<TrayShape> trays)
        {
            var elements = new FilteredElementCollector(linkDoc)
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_CableTray),
                    new ElementCategoryFilter(BuiltInCategory.OST_CableTrayFitting)))
                .WhereElementIsNotElementType();

            foreach (var e in elements)
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                var corners = Corners(bb).Select(transform.OfPoint).ToList();
                double top = corners.Max(p => p.Z);
                double bottom = corners.Min(p => p.Z);
                if (top < zMin || bottom > zMax) continue;

                var shape = new TrayShape
                {
                    Id = e.Id,
                    LinkName = linkName,
                    Top = top,
                    MinX = corners.Min(p => p.X), MinY = corners.Min(p => p.Y),
                    MaxX = corners.Max(p => p.X), MaxY = corners.Max(p => p.Y)
                };
                // Прямой лоток — ось с полушириной: bbox наклонного в плане лотка сильно больше самого лотка
                if (e is CableTray tray && tray.Location is LocationCurve lc && lc.Curve is Line line)
                {
                    shape.IsSegment = true;
                    shape.A = transform.OfPoint(line.GetEndPoint(0));
                    shape.B = transform.OfPoint(line.GetEndPoint(1));
                    shape.HalfWidth = tray.Width / 2;
                }
                trays.Add(shape);
            }
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
        {
            // Transform самого bbox (для элементов обычно тождественный)
            var t = bb.Transform ?? Transform.Identity;
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                yield return t.OfPoint(new XYZ(x, y, z));
        }

        // ---- Типы и геометрия в плане ----

        private sealed class HostElement
        {
            public ElementId Id;
            public string Name;
            public string Category;
            public string LevelName;
            public double MinX, MinY, MaxX, MaxY;
            public double Bottom, CenterZ;
        }

        private sealed class Finding
        {
            public HostElement Element;
            public TrayShape Tray;
            public double Gap;
        }

        private sealed class TrayShape
        {
            public ElementId Id;
            public string LinkName;
            public double Top;
            public double MinX, MinY, MaxX, MaxY;
            public bool IsSegment;
            public XYZ A, B;
            public double HalfWidth;

            public bool OverlapsInPlan(double minX, double minY, double maxX, double maxY)
            {
                bool boxes = MinX <= maxX && MaxX >= minX && MinY <= maxY && MaxY >= minY;
                if (!boxes || !IsSegment) return boxes;
                return SegmentToRectDistance(A.X, A.Y, B.X, B.Y, minX, minY, maxX, maxY) <= HalfWidth;
            }
        }

        /// <summary>Расстояние в плане от отрезка до прямоугольника (0 — пересекаются).</summary>
        private static double SegmentToRectDistance(double ax, double ay, double bx, double by,
            double minX, double minY, double maxX, double maxY)
        {
            if (SegmentIntersectsRect(ax, ay, bx, by, minX, minY, maxX, maxY)) return 0;
            double d = Math.Min(PointToRect(ax, ay, minX, minY, maxX, maxY), PointToRect(bx, by, minX, minY, maxX, maxY));
            d = Math.Min(d, PointToSegment(minX, minY, ax, ay, bx, by));
            d = Math.Min(d, PointToSegment(minX, maxY, ax, ay, bx, by));
            d = Math.Min(d, PointToSegment(maxX, minY, ax, ay, bx, by));
            d = Math.Min(d, PointToSegment(maxX, maxY, ax, ay, bx, by));
            return d;
        }

        // Отсечение Лианга — Барски
        private static bool SegmentIntersectsRect(double ax, double ay, double bx, double by,
            double minX, double minY, double maxX, double maxY)
        {
            double dx = bx - ax, dy = by - ay;
            double t0 = 0, t1 = 1;
            return Clip(-dx, ax - minX, ref t0, ref t1)
                   && Clip(dx, maxX - ax, ref t0, ref t1)
                   && Clip(-dy, ay - minY, ref t0, ref t1)
                   && Clip(dy, maxY - ay, ref t0, ref t1);
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            double r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }

        private static double PointToRect(double x, double y, double minX, double minY, double maxX, double maxY)
        {
            double dx = Math.Max(Math.Max(minX - x, 0), x - maxX);
            double dy = Math.Max(Math.Max(minY - y, 0), y - maxY);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double PointToSegment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / len2));
            double cx = ax + t * dx - px, cy = ay + t * dy - py;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        /// <summary>Сетка лотков в плане по их габаритам, чтобы не сравнивать каждый элемент с каждым лотком.</summary>
        private sealed class TrayGrid
        {
            private readonly Dictionary<(long, long), List<TrayShape>> _cells = new Dictionary<(long, long), List<TrayShape>>();

            public TrayGrid(IEnumerable<TrayShape> trays)
            {
                foreach (var tray in trays)
                    foreach (var key in Cells(tray.MinX, tray.MinY, tray.MaxX, tray.MaxY))
                    {
                        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<TrayShape>();
                        list.Add(tray);
                    }
            }

            public IEnumerable<TrayShape> Near(double minX, double minY, double maxX, double maxY)
            {
                var seen = new HashSet<TrayShape>();
                foreach (var key in Cells(minX, minY, maxX, maxY))
                    if (_cells.TryGetValue(key, out var list))
                        foreach (var tray in list)
                            if (seen.Add(tray)) yield return tray;
            }

            private static IEnumerable<(long, long)> Cells(double minX, double minY, double maxX, double maxY)
            {
                long x0 = (long)Math.Floor(minX / CellSize), x1 = (long)Math.Floor(maxX / CellSize);
                long y0 = (long)Math.Floor(minY / CellSize), y1 = (long)Math.Floor(maxY / CellSize);
                for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                        yield return (x, y);
            }
        }
    }
}
