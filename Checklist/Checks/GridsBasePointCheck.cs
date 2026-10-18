using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;
using Grid = Autodesk.Revit.DB.Grid;

namespace TNovUtils.Checklist.Checks
{
    public sealed class GridsBasePointCheck : ObservableObject, ICheck
    {
        public const string CheckId = "grids-base-point";
        public const string DisplayTitle = Report.ChecklistCatalog.GridsBasePointTitle;
        public const string ResultTitle = "Оси А и 1 пересекаются в базовой точке";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.GridsBasePointNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public GridsBasePointCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.GridsBasePointNumber,
            DisplayTitle,
            ResultTitle,
            GridsBasePointChecker.Run);

        public CheckRunResult Run(Document doc) => GridsBasePointChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.GridsBasePointNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Пересечение осей «А» (или «Ас») и «1» (или «1с») в плане должно совпадать с базовой точкой проекта
    /// (допуск 1 мм). Имя оси «А» принимается и латиницей. Модель не меняет.
    /// </summary>
    public static class GridsBasePointChecker
    {
        private const double FeetToMm = 304.8;
        private const double ToleranceMm = 1;

        // Порядок — приоритет: сначала основное имя, потом «с»; латинские двойники кириллицы тоже принимаются
        private static readonly string[] LetterNames = { "А", "A", "Ас", "Ac", "АС", "AC" };
        private static readonly string[] DigitNames = { "1", "1с", "1c", "1С", "1C" };

        public static CheckRunResult Run(Document doc)
        {
            var log = new StringBuilder();
            var ids = new List<ElementId>();
            bool passed = false;

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .ToList();
            var gridA = Find(grids, LetterNames);
            var grid1 = Find(grids, DigitNames);

            log.AppendLine();
            if (gridA == null) log.AppendLine("Ось «А» («Ас») не найдена");
            if (grid1 == null) log.AppendLine("Ось «1» («1с») не найдена");

            var basePoint = new FilteredElementCollector(doc)
                .OfClass(typeof(BasePoint))
                .Cast<BasePoint>()
                .FirstOrDefault(p => !p.IsShared);
            if (basePoint == null) log.AppendLine("Базовая точка проекта не найдена");

            if (gridA != null && grid1 != null && basePoint != null)
            {
                ids.Add(gridA.Id);
                ids.Add(grid1.Id);
                XYZ bp = basePoint.Position;
                log.AppendLine($"Оси: «{gridA.Name}» Id {ElementIds.ToStringValue(gridA.Id)}, «{grid1.Name}» Id {ElementIds.ToStringValue(grid1.Id)}");
                log.AppendLine($"Базовая точка: X {Mm(bp.X)} Y {Mm(bp.Y)} мм");

                if (!(gridA.Curve is Line lineA) || !(grid1.Curve is Line line1))
                {
                    log.AppendLine("Одна из осей — дуга, пересечение не проверяется");
                }
                else if (!TryIntersect(lineA, line1, out double x, out double y))
                {
                    log.AppendLine("Оси параллельны — не пересекаются");
                }
                else
                {
                    double dx = (x - bp.X) * FeetToMm, dy = (y - bp.Y) * FeetToMm;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    log.AppendLine($"Пересечение осей: X {Mm(x)} Y {Mm(y)} мм");
                    passed = distance <= ToleranceMm;
                    log.AppendLine(passed
                        ? "Пересечение совпадает с базовой точкой"
                        : $"Пересечение смещено от базовой точки на {distance:0.#} мм (ΔX {dx:0.#}, ΔY {dy:0.#})");
                }
            }
            else
            {
                if (gridA != null) ids.Add(gridA.Id);
                if (grid1 != null) ids.Add(grid1.Id);
            }

            return new CheckRunResult
            {
                Title = GridsBasePointCheck.ResultTitle,
                Passed = passed,
                ElemIds = passed ? "" : string.Join(", ", ids.Select(ElementIds.ToStringValue)),
                Log = log.ToString()
            };
        }

        private static Grid Find(List<Grid> grids, string[] names)
        {
            foreach (var name in names)
            {
                var grid = grids.FirstOrDefault(g => string.Equals((g.Name ?? "").Trim(), name, StringComparison.Ordinal));
                if (grid != null) return grid;
            }
            return null;
        }

        /// <summary>Пересечение прямых осей в плане (оси продлеваются).</summary>
        private static bool TryIntersect(Line a, Line b, out double x, out double y)
        {
            XYZ p = a.GetEndPoint(0), r = a.GetEndPoint(1) - p;
            XYZ q = b.GetEndPoint(0), s = b.GetEndPoint(1) - q;
            double cross = r.X * s.Y - r.Y * s.X;
            x = y = 0;
            if (Math.Abs(cross) < 1e-9) return false;
            double t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / cross;
            x = p.X + t * r.X;
            y = p.Y + t * r.Y;
            return true;
        }

        private static string Mm(double feet) => (feet * FeetToMm).ToString("0.#");
    }
}
