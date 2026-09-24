using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class WetRoomsOverElectricalCheck : ObservableObject, ICheck
    {
        public const string CheckId = "wet-rooms-over-electrical";
        public const string DisplayTitle = Report.ChecklistCatalog.WetRoomsOverElectricalTitle;
        public const string ResultTitle = "Влажных помещений над электрощитовыми и узлами связи нет";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.WetRoomsOverElectricalNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public WetRoomsOverElectricalCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.WetRoomsOverElectricalNumber,
            DisplayTitle,
            ResultTitle,
            WetRoomsOverElectricalChecker.Run);

        public CheckRunResult Run(Document doc) => WetRoomsOverElectricalChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.WetRoomsOverElectricalNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Над электрощитовыми и узлами связи не должно быть влажных помещений (санузлы, ПУИ, ванные).
    /// Влажное помещение считается «над», если его низ выше низа электрощитовой больше чем на 1500 мм
    /// (другой этаж) и не выше её верхней границы плюс 1500 мм (перекрытие), а контуры помещений
    /// в плане пересекаются по площади не меньше 0,1 м². Контуры — по чистовой отделке. Модель не меняет.
    /// </summary>
    public static class WetRoomsOverElectricalChecker
    {
        private const double SameFloorTolerance = 1500 / 304.8;
        private const double MaxSlabThickness = 1500 / 304.8;
        private const double SqFtPerSqM = 10.7639104;
        private const double MinOverlapArea = 0.1 * SqFtPerSqM;

        // Целое слово, без учёта регистра: «Санузел 2», «С/у», «ПУИ», но не часть другого слова
        private static readonly Regex ElectricalName = NameRegex(@"электрощитовая|узел\s+связи");
        private static readonly Regex WetName = NameRegex(@"санузел|с\s*[/\\]\s*у|пуи|помещение\s+уборочного\s+инвентаря|ванная");

        private static Regex NameRegex(string words) => new Regex(
            @"(?<![\p{L}\p{Nd}])(" + words + @")(?![\p{L}\p{Nd}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static CheckRunResult Run(Document doc)
        {
            var log = new StringBuilder();
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Location != null && r.Area > 0 && r.Level != null)
                .ToList();

            var electrical = rooms.Where(r => ElectricalName.IsMatch(RoomName(r))).Select(r => new RoomInfo(r)).ToList();
            var wet = rooms.Where(r => WetName.IsMatch(RoomName(r))).Select(r => new RoomInfo(r)).ToList();

            if (electrical.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("В модели нет помещений «Электрощитовая», «Узел связи» — проверять нечего");
                return Result(new List<Finding>(), log);
            }
            if (wet.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("В модели нет влажных помещений (санузлы, ПУИ, ванные) — проверять нечего");
                return Result(new List<Finding>(), log);
            }

            var findings = new List<Finding>();
            var failed = new HashSet<RoomInfo>();
            foreach (var e in electrical)
            {
                foreach (var w in wet)
                {
                    if (w.Base <= e.Base + SameFloorTolerance || w.Base > e.Top + MaxSlabThickness) continue;
                    if (!e.BoxOverlaps(w)) continue;

                    var es = e.GetPlanSolid();
                    var ws = w.GetPlanSolid();
                    if (es == null) { failed.Add(e); continue; }
                    if (ws == null) { failed.Add(w); continue; }

                    double area = OverlapArea(es, ws);
                    if (area < 0) { failed.Add(e); failed.Add(w); continue; }
                    if (area >= MinOverlapArea)
                        findings.Add(new Finding { Electrical = e, Wet = w, Area = area });
                }
            }

            if (failed.Count > 0)
            {
                log.AppendLine();
                log.AppendLine("Не удалось построить контур помещений, пересечение не проверено:");
                foreach (var r in failed.OrderBy(r => r.Label, StringComparer.Ordinal))
                    log.AppendLine($"  {ElementIds.ToStringValue(r.Room.Id)} {r.Label}");
            }

            return Result(findings, log);
        }

        private static CheckRunResult Result(List<Finding> findings, StringBuilder log)
        {
            if (findings.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Влажные помещения над электрощитовыми и узлами связи: {findings.Count}");
                foreach (var g in findings.GroupBy(f => f.Electrical).OrderBy(g => g.Key.Label, StringComparer.Ordinal))
                {
                    log.AppendLine();
                    log.AppendLine($"Над {ElementIds.ToStringValue(g.Key.Room.Id)} {g.Key.Label}:");
                    foreach (var f in g.OrderBy(f => f.Wet.Label, StringComparer.Ordinal))
                        log.AppendLine($"  {ElementIds.ToStringValue(f.Wet.Room.Id)} {f.Wet.Label} — пересечение {f.Area / SqFtPerSqM:0.##} м²");
                    log.AppendLine("Id: " + string.Join(", ",
                        new[] { g.Key }.Concat(g.Select(f => f.Wet)).Select(r => ElementIds.ToStringValue(r.Room.Id))));
                }
            }

            var ids = findings
                .SelectMany(f => new[] { f.Wet, f.Electrical })
                .Select(r => ElementIds.ToStringValue(r.Room.Id))
                .Distinct()
                .ToList();
            return new CheckRunResult
            {
                Title = WetRoomsOverElectricalCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log.ToString()
            };
        }

        /// <summary>Площадь пересечения в плане (кв. футы) через объём пересечения выдавливаний высотой 1 фут; -1 — ошибка.</summary>
        private static double OverlapArea(Solid a, Solid b)
        {
            try
            {
                var common = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect);
                return common == null ? 0 : common.Volume;
            }
            catch
            {
                return -1;
            }
        }

        private static string RoomName(Room room) =>
            room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name ?? "";

        private sealed class Finding
        {
            public RoomInfo Electrical;
            public RoomInfo Wet;
            public double Area;
        }

        private sealed class RoomInfo
        {
            public readonly Room Room;
            public readonly string Label;
            public readonly double Base, Top;
            private readonly BoundingBoxXYZ _box;
            private Solid _planSolid;
            private bool _solidBuilt;

            public RoomInfo(Room room)
            {
                Room = room;
                Base = room.Level.ProjectElevation + room.BaseOffset;
                Top = Base + room.UnboundedHeight;
                _box = room.get_BoundingBox(null);

                string number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
                string name = RoomName(room);
                string label = string.IsNullOrWhiteSpace(number) ? name : number + " " + name;
                Label = (label.Trim() + " (" + room.Level.Name + ")").Trim();
            }

            /// <summary>Быстрый отсев по габаритам в плане; без габаритов — считаем, что могут пересекаться.</summary>
            public bool BoxOverlaps(RoomInfo other)
            {
                if (_box == null || other._box == null) return true;
                return _box.Min.X <= other._box.Max.X && _box.Max.X >= other._box.Min.X
                    && _box.Min.Y <= other._box.Max.Y && _box.Max.Y >= other._box.Min.Y;
            }

            /// <summary>Контур помещения, опущенный на Z = 0 и выдавленный на 1 фут; null — контур не построен.</summary>
            public Solid GetPlanSolid()
            {
                if (_solidBuilt) return _planSolid;
                _solidBuilt = true;
                try
                {
                    var options = new SpatialElementBoundaryOptions
                    {
                        SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
                    };
                    var loops = new List<CurveLoop>();
                    foreach (var segments in Room.GetBoundarySegments(options) ?? new List<IList<BoundarySegment>>())
                    {
                        var loop = new CurveLoop();
                        foreach (var s in segments)
                        {
                            var curve = s.GetCurve();
                            loop.Append(curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, -curve.GetEndPoint(0).Z))));
                        }
                        if (loop.Any()) loops.Add(loop);
                    }
                    if (loops.Count > 0)
                        _planSolid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 1.0);
                }
                catch
                {
                    _planSolid = null;
                }
                return _planSolid;
            }
        }
    }
}
