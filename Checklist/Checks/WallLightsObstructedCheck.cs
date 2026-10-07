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
    public sealed class WallLightsObstructedCheck : ObservableObject, ICheck
    {
        public const string CheckId = "wall-lights-obstructed";
        public const string DisplayTitle = Report.ChecklistCatalog.WallLightsObstructedTitle;
        public const string ResultTitle = "Светильников на стенах, перекрытых трубами, воздуховодами и лотками, не найдено";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.WallLightsObstructedNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public WallLightsObstructedCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.WallLightsObstructedNumber,
            DisplayTitle,
            ResultTitle,
            doc => WallLightsObstructedChecker.Run(doc, allowUi: true));

        public CheckRunResult Run(Document doc) => WallLightsObstructedChecker.Run(doc, allowUi: true);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.WallLightsObstructedNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Светильники на стенах модели ЭЛ не должны быть перекрыты трубами, воздуховодами и кабельными лотками.
    /// Светильник на стене — размещённый на вертикальной грани или плоскости (светит по нормали от неё)
    /// либо на основе стены (светит от стены в сторону, где стоит светильник),
    /// либо с включённым параметром «Настенный» (семейства pmN.Светильник; светит по лицевой стороне семейства).
    /// Перед светильником строится коридор сечением в габарит светильника и глубиной 1000 мм от его лицевой
    /// стороны; ошибка — если коридор пересекает трубу, воздуховод, лоток, их фитинги или арматуру
    /// в текущей модели или в связях ВК/ОВ/СС. Выгруженные связи только отмечаются в логе. Модель не меняет.
    /// </summary>
    public static class WallLightsObstructedChecker
    {
        private const double Depth = 1000 / 304.8;
        private const double MinSize = 10 / 304.8;
        // |Z| нормали, при котором грань размещения считаем вертикальной (~6°)
        private const double VerticalTolerance = 0.1;
        private const int MaxObstaclesInLog = 5;
        private const string WallMountedParameter = "Настенный";

        private static readonly List<BuiltInCategory> ObstacleCategories = new List<BuiltInCategory>
        {
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_FlexDuctCurves,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_CableTrayFitting
        };

        public static CheckRunResult Run(Document doc, bool allowUi)
        {
            var log = new StringBuilder();

            var lights = CollectLights(doc);
            if (lights.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("В модели нет светильников на стенах — проверять нечего");
                return Result(new List<Finding>(), log);
            }
            log.AppendLine();
            log.AppendLine($"Светильников на стенах: {lights.Count}");

            var sources = new List<LoadedLink>
            {
                new LoadedLink { Document = doc, Transform = Transform.Identity, Name = "текущая модель" }
            };
            sources.AddRange(LinkLoading.CollectLoaded(doc, Report.ChecklistCatalog.VkOvSsMarkers, "ВК/ОВ/СС", allowUi, log));

            var categoryFilter = new ElementMulticategoryFilter(ObstacleCategories);
            var findings = new List<Finding>();
            var failed = new List<Light>();
            foreach (var light in lights)
            {
                var obstacles = new List<Obstacle>();
                bool lightFailed = false;
                foreach (var source in sources)
                {
                    try
                    {
                        var toSource = source.Transform.Inverse;
                        var corners = light.Corners().Select(toSource.OfPoint).ToList();
                        var outline = new Outline(
                            new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
                            new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)));
                        var solid = light.CorridorSolid(toSource);

                        var found = new FilteredElementCollector(source.Document)
                            .WherePasses(categoryFilter)
                            .WhereElementIsNotElementType()
                            .WherePasses(new BoundingBoxIntersectsFilter(outline))
                            .WherePasses(new ElementIntersectsSolidFilter(solid));
                        foreach (var e in found)
                            obstacles.Add(new Obstacle { Id = e.Id, Category = e.Category?.Name ?? "", LinkName = source.Name });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Чек-лист: светильник {ElementIds.ToStringValue(light.Id)}, {source.Name}: {ex.Message}", 3);
                        lightFailed = true;
                    }
                }
                if (lightFailed) failed.Add(light);
                if (obstacles.Count > 0) findings.Add(new Finding { Light = light, Obstacles = obstacles });
            }

            if (failed.Count > 0)
            {
                log.AppendLine();
                log.AppendLine("Не удалось проверить пространство перед светильниками:");
                foreach (var l in failed.OrderBy(l => l.Name, StringComparer.Ordinal))
                    log.AppendLine($"  {ElementIds.ToStringValue(l.Id)} {l.Name}");
            }

            return Result(findings, log);
        }

        private static CheckRunResult Result(List<Finding> findings, StringBuilder log)
        {
            if (findings.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Перед светильниками (до {Depth * 304.8:0} мм) трубы, воздуховоды или лотки, светильников: {findings.Count}");
                foreach (var g in findings.GroupBy(f => f.Light.LevelName).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    log.AppendLine();
                    log.AppendLine($"{g.Key} — {g.Count()} шт.");
                    foreach (var f in g.OrderBy(f => f.Light.Name, StringComparer.Ordinal))
                    {
                        var list = f.Obstacles
                            .Take(MaxObstaclesInLog)
                            .Select(o => $"{o.Category} ({o.LinkName}) Id {ElementIds.ToStringValue(o.Id)}");
                        string more = f.Obstacles.Count > MaxObstaclesInLog ? $" и ещё {f.Obstacles.Count - MaxObstaclesInLog}" : "";
                        log.AppendLine($"  {ElementIds.ToStringValue(f.Light.Id)} {f.Light.Name} — {string.Join(", ", list)}{more}");
                    }
                    log.AppendLine("Id: " + string.Join(", ", g.Select(f => ElementIds.ToStringValue(f.Light.Id))));
                }
            }

            var ids = findings.Select(f => ElementIds.ToStringValue(f.Light.Id)).Distinct().ToList();
            return new CheckRunResult
            {
                Title = WallLightsObstructedCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log.ToString()
            };
        }

        // ---- Светильники ----

        private static List<Light> CollectLights(Document doc)
        {
            var result = new List<Light>();
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .Cast<FamilyInstance>();

            foreach (var fi in instances)
            {
                // Вложенный светильник проверяется в составе родительского
                if (fi.SuperComponent is FamilyInstance parent && parent.Category != null
                    && parent.Category.Id.Equals(new ElementId(BuiltInCategory.OST_LightingFixtures)))
                    continue;

                var points = GeometryPoints(fi);
                if (points.Count == 0) continue;
                var center = new XYZ(
                    (points.Min(p => p.X) + points.Max(p => p.X)) / 2,
                    (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2,
                    (points.Min(p => p.Z) + points.Max(p => p.Z)) / 2);

                var front = FrontDirection(fi, center);
                if (front == null) continue;

                result.Add(new Light(fi.Id, $"{fi.Symbol?.FamilyName}: {fi.Name}", LevelName(doc, fi), front, points));
            }
            return result;
        }

        /// <summary>Направление, куда светит светильник на стене (горизонтальное); null — светильник не на стене.</summary>
        private static XYZ FrontDirection(FamilyInstance fi, XYZ center)
        {
            // На грани или рабочей плоскости: локальная Z — нормаль от грани
            var z = fi.GetTransform().BasisZ;
            if (Math.Abs(z.Z) < VerticalTolerance) return Horizontal(z);

            // Параметр «Настенный» (семейства pmN.Светильник): светит по лицевой стороне семейства
            if (!(fi.Host is Wall wall) || !(wall.Location is LocationCurve lc))
                return IsWallMountedByParameter(fi) ? FacingFront(fi, center) : null;

            // На основе стены: перпендикуляр к оси стены в сторону светильника
            var curve = lc.Curve;
            var projection = curve.Project(new XYZ(center.X, center.Y, curve.GetEndPoint(0).Z));
            if (projection == null) return Horizontal(fi.FacingOrientation);

            var normal = Horizontal(XYZ.BasisZ.CrossProduct(curve.ComputeDerivatives(projection.Parameter, false).BasisX));
            if (normal == null) return Horizontal(fi.FacingOrientation);
            double side = normal.DotProduct(new XYZ(center.X - projection.XYZPoint.X, center.Y - projection.XYZPoint.Y, 0));
            if (Math.Abs(side) < 1e-6) side = normal.DotProduct(fi.FacingOrientation);
            return side >= 0 ? normal : normal.Negate();
        }

        private static bool IsWallMountedByParameter(FamilyInstance fi)
        {
            var p = fi.LookupParameter(WallMountedParameter) ?? fi.Symbol?.LookupParameter(WallMountedParameter);
            return p != null && p.StorageType == StorageType.Integer && p.AsInteger() == 1;
        }

        /// <summary>
        /// Лицевая сторона семейства; знак — в сторону, куда геометрия вынесена от точки вставки
        /// (точка вставки настенного светильника обычно у стены). Геометрия по центру — как у семейства.
        /// </summary>
        private static XYZ FacingFront(FamilyInstance fi, XYZ center)
        {
            var facing = Horizontal(fi.FacingOrientation);
            if (facing == null) return null;
            var origin = fi.GetTransform().Origin;
            double offset = facing.DotProduct(new XYZ(center.X - origin.X, center.Y - origin.Y, 0));
            return offset < -MinSize ? facing.Negate() : facing;
        }

        private static XYZ Horizontal(XYZ v)
        {
            if (v == null) return null;
            var h = new XYZ(v.X, v.Y, 0);
            return h.GetLength() < 1e-9 ? null : h.Normalize();
        }

        private static string LevelName(Document doc, FamilyInstance fi)
        {
            var levelId = fi.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
                levelId = fi.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)?.AsElementId();
            return (levelId != null ? doc.GetElement(levelId) as Level : null)?.Name ?? "Без уровня";
        }

        /// <summary>Точки видимой 3D-геометрии в координатах модели; без геометрии — углы габарита.</summary>
        private static List<XYZ> GeometryPoints(FamilyInstance fi)
        {
            var points = new List<XYZ>();
            try
            {
                var geometry = fi.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                if (geometry != null) CollectPoints(geometry, points);
            }
            catch
            {
                points.Clear();
            }
            if (points.Count > 0) return points;

            var bb = fi.get_BoundingBox(null);
            if (bb == null) return points;
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                points.Add(new XYZ(x, y, z));
            return points;
        }

        private static void CollectPoints(GeometryElement geometry, List<XYZ> points)
        {
            foreach (var obj in geometry)
            {
                switch (obj)
                {
                    case Solid solid when solid.Edges.Size > 0:
                        foreach (Edge edge in solid.Edges)
                            points.AddRange(edge.Tessellate());
                        break;
                    case Mesh mesh:
                        points.AddRange(mesh.Vertices);
                        break;
                    case GeometryInstance instance:
                        CollectPoints(instance.GetInstanceGeometry(), points);
                        break;
                }
            }
        }

        // ---- Типы ----

        private sealed class Finding
        {
            public Light Light;
            public List<Obstacle> Obstacles;
        }

        private sealed class Obstacle
        {
            public ElementId Id;
            public string Category;
            public string LinkName;
        }

        /// <summary>
        /// Светильник и коридор перед ним: сечение — габарит светильника поперёк направления света,
        /// от середины светильника по глубине до его лицевой стороны плюс Depth.
        /// </summary>
        private sealed class Light
        {
            public readonly ElementId Id;
            public readonly string Name;
            public readonly string LevelName;
            private readonly XYZ _front, _side;
            private readonly double _s0, _s1, _u0, _u1, _d0, _d1;

            public Light(ElementId id, string name, string levelName, XYZ front, List<XYZ> points)
            {
                Id = id;
                Name = name;
                LevelName = levelName;
                _front = front;
                // (side, Z, front) — правая тройка: контур сечения против часовой стрелки относительно front
                _side = XYZ.BasisZ.CrossProduct(front);

                var s = points.Select(p => p.DotProduct(_side)).ToList();
                var u = points.Select(p => p.Z).ToList();
                var d = points.Select(p => p.DotProduct(front)).ToList();
                (_s0, _s1) = Range(s.Min(), s.Max());
                (_u0, _u1) = Range(u.Min(), u.Max());
                _d0 = (d.Min() + d.Max()) / 2;
                _d1 = d.Max() + Depth;
            }

            private static (double, double) Range(double min, double max)
            {
                if (max - min >= MinSize) return (min, max);
                double mid = (min + max) / 2;
                return (mid - MinSize / 2, mid + MinSize / 2);
            }

            private XYZ Point(double s, double u, double d) => _side * s + XYZ.BasisZ * u + _front * d;

            private XYZ[] Section(double d) => new[]
            {
                Point(_s0, _u0, d), Point(_s1, _u0, d), Point(_s1, _u1, d), Point(_s0, _u1, d)
            };

            public IEnumerable<XYZ> Corners() => Section(_d0).Concat(Section(_d1));

            public Solid CorridorSolid(Transform transform)
            {
                var pts = Section(_d0).Select(transform.OfPoint).ToArray();
                var loop = new CurveLoop();
                for (int i = 0; i < pts.Length; i++)
                    loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Length]));
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, transform.OfVector(_front), _d1 - _d0);
            }
        }
    }
}
