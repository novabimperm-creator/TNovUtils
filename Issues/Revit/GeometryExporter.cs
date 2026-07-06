using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;

namespace TNovUtils.Issues.Revit
{
    /// <summary>
    /// Экспортирует геометрию выбранного элемента (элементов) замечания и его ближайшего
    /// окружения (соседей) в self-contained бинарный glTF (.glb) для просмотра в браузерном
    /// вьюере (three.js). Координаты: Revit (футы, Z-up) → glTF (метры, Y-up), с рецентрированием
    /// в центр выделения (иначе мировые координаты Revit ломают float-точность).
    ///
    /// Роли вершин кодируются цветом материала:
    ///   target   — выбранный элемент замечания, красный;
    ///   neighbor — соседи в расширенном bbox (ближайшее окружение), серый полупрозрачный.
    ///
    /// glTF собирается вручную (без SharpGLTF и т.п.), чтобы не тащить в процесс Revit
    /// лишние сборки и не ловить конфликты версий. Геометрия неиндексированная
    /// (3 вершины на треугольник, плоские нормали) — проще и надёжнее для фрагмента.
    /// </summary>
    public static class GeometryExporter
    {
        private const double FeetToMeters = 0.3048;
        private const double PadFeet = 8.0;          // ~2.4 м зона сбора соседей-кандидатов
        private const int MaxNeighbors = 150;        // берём только ближайшие N (окружение, с которым контактирует элемент)

        // Детализация триангуляции: сам элемент замечания — среднее качество (его смотрят
        // в упор), соседи-окружение — грубое (Coarse), чтобы 150 элементов уложились в
        // серверный лимит .glb (20 МБ) и вьювер не тормозил.
        private const ViewDetailLevel TargetDetail = ViewDetailLevel.Medium;
        private const ViewDetailLevel NeighborDetail = ViewDetailLevel.Coarse;

        public sealed class ExportResult
        {
            public byte[] Glb;
            public int TriangleCount;
            public int NeighborCount;
            public bool Truncated;   // соседей было больше MaxNeighbors
        }

        /// <param name="selectedIds">Выбранные элементы замечания — все идут в роль target; их ближайшее окружение добавляется как neighbor.</param>
        public static ExportResult Export(Document doc, IList<long> selectedIds)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (selectedIds == null || selectedIds.Count == 0)
                throw new InvalidOperationException("Не передан ни один ElementId.");

            var selectedSet = new HashSet<long>(selectedIds);

            // Базовые элементы выделения.
            var baseElems = new List<Element>();
            foreach (var idv in selectedIds.Distinct())
            {
                var el = doc.GetElement(MakeElementId(idv));
                if (el != null) baseElems.Add(el);
            }
            if (baseElems.Count == 0)
                throw new InvalidOperationException("Выделенные элементы не найдены в текущей модели.");

            // Объединённый bbox выделения → центр (для рецентрирования) и расширенный bbox (для соседей).
            var union = UnionBox(baseElems);
            if (union == null)
                throw new InvalidOperationException("У выделенных элементов нет геометрии (BoundingBox).");

            var center = (union.Min + union.Max) * 0.5;

            // Соседи-кандидаты: модельные элементы в небольшой зоне вокруг выделения.
            // Из них берём только БЛИЖАЙШИЕ MaxNeighbors (по расстоянию центра bbox до центра
            // выделения) — иначе в плотной модели 3D-фрагмент разрастается на всё вокруг.
            var min = new XYZ(union.Min.X - PadFeet, union.Min.Y - PadFeet, union.Min.Z - PadFeet);
            var max = new XYZ(union.Max.X + PadFeet, union.Max.Y + PadFeet, union.Max.Z + PadFeet);
            var filter = new BoundingBoxIntersectsFilter(new Outline(min, max));

            var candidates = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .ToElements()
#if R2027
                .Where(el => !selectedSet.Contains(el.Id.Value))
#else
                .Where(el => !selectedSet.Contains(el.Id.IntegerValue))
#endif
                .Where(el => el.Category != null && el.Category.CategoryType == CategoryType.Model)
                .Select(el => new { el, dist = DistanceToCenter(el, center) })
                .OrderBy(x => x.dist)
                .Select(x => x.el)
                .ToList();

            // Накопители. Каждый выбранный элемент замечания — ОТДЕЛЬНЫЙ меш со своим
            // ElementId: веб-вьювер сопоставляет роль (Id1 → красный, Id2 → оранжевый)
            // с мешем, ища ElementId как подстроку в имени узла/меша и в extras (userData).
            // Соседи (окружение) в подсветке не участвуют → объединяем в один серый меш.
            var meshList = new List<RoleMesh>();
            var neighborMesh = new RoleMesh("neighbor");

            int neighborCount = 0;

            // Выбранные элементы замечания — по мешу на элемент, роль target.
            foreach (var idv in selectedIds.Distinct())
            {
                var rm = new RoleMesh("target", idv);
                if (AddElement(doc.GetElement(MakeElementId(idv)), rm, center, TargetDetail))
                    meshList.Add(rm);
            }

            // Соседи: идём от ближайшего к дальнему и останавливаемся на MaxNeighbors
            // элементах, из которых реально извлеклась геометрия.
            foreach (var el in candidates)
            {
                if (neighborCount >= MaxNeighbors) break;
                if (AddElement(el, neighborMesh, center, NeighborDetail)) neighborCount++;
            }
            bool truncated = neighborCount >= MaxNeighbors && candidates.Count > neighborCount;
            if (neighborMesh.Positions.Count > 0) meshList.Add(neighborMesh);

            var nonEmpty = meshList.Where(m => m.Positions.Count > 0).ToList();
            if (nonEmpty.Count == 0)
                throw new InvalidOperationException("Не удалось извлечь триангулированную геометрию выделения.");

            int triangleCount = nonEmpty.Sum(m => m.Positions.Count / 9); // 9 float = 3 вершины = 1 треугольник
            var glb = BuildGlb(nonEmpty);

            return new ExportResult
            {
                Glb = glb,
                TriangleCount = triangleCount,
                NeighborCount = neighborCount,
                Truncated = truncated,
            };
        }

#if R2027
        private static ElementId MakeElementId(long value) => new ElementId(value); // Revit 2024+: long-конструктор
#else
        private static ElementId MakeElementId(long value) => new ElementId(unchecked((int)value));
#endif

        /// <summary>Расстояние от центра bbox элемента до центра выделения (для отбора ближайших соседей). Нет геометрии → +∞.</summary>
        private static double DistanceToCenter(Element el, XYZ center)
        {
            var bb = el.get_BoundingBox(null);
            if (bb == null) return double.MaxValue;
            var c = (bb.Min + bb.Max) * 0.5;
            return c.DistanceTo(center);
        }

        private static BoundingBoxXYZ UnionBox(IEnumerable<Element> elems)
        {
            double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
            double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
            bool any = false;
            foreach (var el in elems)
            {
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                any = true;
                minx = Math.Min(minx, bb.Min.X); miny = Math.Min(miny, bb.Min.Y); minz = Math.Min(minz, bb.Min.Z);
                maxx = Math.Max(maxx, bb.Max.X); maxy = Math.Max(maxy, bb.Max.Y); maxz = Math.Max(maxz, bb.Max.Z);
            }
            if (!any) return null;
            return new BoundingBoxXYZ { Min = new XYZ(minx, miny, minz), Max = new XYZ(maxx, maxy, maxz) };
        }

        /// <summary>Триангулирует элемент и складывает плоско-затенённые треугольники в RoleMesh. true — добавил хоть что-то.</summary>
        private static bool AddElement(Element el, RoleMesh mesh, XYZ center, ViewDetailLevel detail)
        {
            if (el == null) return false;
            var opt = new Options { ComputeReferences = false, DetailLevel = detail };
            GeometryElement ge;
            try { ge = el.get_Geometry(opt); }
            catch { return false; }
            if (ge == null) return false;

            int before = mesh.Positions.Count;
            CollectTriangles(ge, (a, b, c) =>
            {
                var ga = ToGltf(a, center);
                var gb = ToGltf(b, center);
                var gc = ToGltf(c, center);

                // Плоская нормаль треугольника (в координатах glTF).
                var n = Normalize(Cross(Sub(gb, ga), Sub(gc, ga)));

                AddVertex(mesh, ga, n);
                AddVertex(mesh, gb, n);
                AddVertex(mesh, gc, n);
            });
            return mesh.Positions.Count > before;
        }

        private static void CollectTriangles(GeometryElement ge, Action<XYZ, XYZ, XYZ> emit)
        {
            foreach (var obj in ge)
            {
                switch (obj)
                {
                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face f in solid.Faces)
                            EmitMesh(f.Triangulate(), emit);
                        break;
                    case Mesh m:
                        EmitMesh(m, emit);
                        break;
                    case GeometryInstance gi:
                        // GetInstanceGeometry() возвращает геометрию уже в координатах модели.
                        var inst = gi.GetInstanceGeometry();
                        if (inst != null) CollectTriangles(inst, emit);
                        break;
                }
            }
        }

        private static void EmitMesh(Mesh m, Action<XYZ, XYZ, XYZ> emit)
        {
            if (m == null) return;
            int n = m.NumTriangles;
            for (int i = 0; i < n; i++)
            {
                var t = m.get_Triangle(i);
                emit(t.get_Vertex(0), t.get_Vertex(1), t.get_Vertex(2));
            }
        }

        // Revit (футы, Z-up) → glTF (метры, Y-up), с рецентрированием в center.
        private static double[] ToGltf(XYZ p, XYZ c) => new[]
        {
            (p.X - c.X) * FeetToMeters,
            (p.Z - c.Z) * FeetToMeters,
            -(p.Y - c.Y) * FeetToMeters,
        };

        private static void AddVertex(RoleMesh mesh, double[] pos, double[] nrm)
        {
            mesh.Positions.Add((float)pos[0]); mesh.Positions.Add((float)pos[1]); mesh.Positions.Add((float)pos[2]);
            mesh.Normals.Add((float)nrm[0]); mesh.Normals.Add((float)nrm[1]); mesh.Normals.Add((float)nrm[2]);
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Cross(double[] a, double[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0],
        };
        private static double[] Normalize(double[] v)
        {
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (len < 1e-12) return new double[] { 0, 1, 0 };
            return new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        private sealed class RoleMesh
        {
            public string Role { get; }
            public long? ElementId { get; }
            public List<float> Positions { get; } = new List<float>();
            public List<float> Normals { get; } = new List<float>();
            public RoleMesh(string role, long? elementId = null) { Role = role; ElementId = elementId; }

            /// <summary>Имя узла/меша в glTF. Для target — «Element_&lt;id&gt;», чтобы веб-вьювер нашёл элемент по подстроке.</summary>
            public string GltfName => ElementId.HasValue ? "Element_" + ElementId.Value : Role;
        }

        private static int RoleToMaterial(string role)
        {
            switch (role)
            {
                case "target": return 0;
                default: return 1; // neighbor
            }
        }

        private static object[] MaterialsJson() => new object[]
        {
            new { name = "target",   pbrMetallicRoughness = new { baseColorFactor = new[] { 0.94, 0.27, 0.27, 1.0 },  metallicFactor = 0.1, roughnessFactor = 0.7 }, doubleSided = true },
            new { name = "neighbor", pbrMetallicRoughness = new { baseColorFactor = new[] { 0.45, 0.48, 0.53, 0.45 }, metallicFactor = 0.1, roughnessFactor = 0.8 }, alphaMode = "BLEND", doubleSided = true },
        };

        private static byte[] BuildGlb(List<RoleMesh> meshes)
        {
            var bin = new MemoryStream();
            var bw = new BinaryWriter(bin);

            void AlignBin()
            {
                while (bin.Length % 4 != 0) bw.Write((byte)0);
            }

            var bufferViews = new List<object>();
            var accessors = new List<object>();
            var gltfMeshes = new List<object>();
            var nodes = new List<object>();
            var nodeIdx = new List<int>();

            foreach (var mesh in meshes)
            {
                int vcount = mesh.Positions.Count / 3;

                // POSITION
                AlignBin();
                int posOffset = (int)bin.Length;
                ComputeMinMax(mesh.Positions, out var pmin, out var pmax);
                foreach (var f in mesh.Positions) bw.Write(f);
                int posLen = (int)bin.Length - posOffset;
                int posView = bufferViews.Count;
                bufferViews.Add(new { buffer = 0, byteOffset = posOffset, byteLength = posLen, target = 34962 });
                int posAcc = accessors.Count;
                accessors.Add(new { bufferView = posView, componentType = 5126, count = vcount, type = "VEC3", min = pmin, max = pmax });

                // NORMAL
                AlignBin();
                int nrmOffset = (int)bin.Length;
                foreach (var f in mesh.Normals) bw.Write(f);
                int nrmLen = (int)bin.Length - nrmOffset;
                int nrmView = bufferViews.Count;
                bufferViews.Add(new { buffer = 0, byteOffset = nrmOffset, byteLength = nrmLen, target = 34962 });
                int nrmAcc = accessors.Count;
                accessors.Add(new { bufferView = nrmView, componentType = 5126, count = vcount, type = "VEC3" });

                int meshIdx = gltfMeshes.Count;
                gltfMeshes.Add(new
                {
                    name = mesh.GltfName,
                    primitives = new[]
                    {
                        new
                        {
                            attributes = new { POSITION = posAcc, NORMAL = nrmAcc },
                            material = RoleToMaterial(mesh.Role),
                        },
                    },
                });

                // Узел: имя = «Element_<id>» (для сопоставления по подстроке) + extras с
                // числовым ElementId (GLTFLoader кладёт extras в mesh.userData → revitElementId).
                var node = new Dictionary<string, object> { ["mesh"] = meshIdx, ["name"] = mesh.GltfName };
                if (mesh.ElementId.HasValue)
                    node["extras"] = new Dictionary<string, object> { ["revitElementId"] = mesh.ElementId.Value };
                nodes.Add(node);
                nodeIdx.Add(nodes.Count - 1);
            }

            bw.Flush();
            byte[] binBytes = bin.ToArray();

            var gltf = new
            {
                asset = new { version = "2.0", generator = "TNovUtils.Issues GLB exporter" },
                scene = 0,
                scenes = new[] { new { nodes = nodeIdx.ToArray() } },
                nodes,
                meshes = gltfMeshes,
                materials = MaterialsJson(),
                accessors,
                bufferViews,
                buffers = new[] { new { byteLength = binBytes.Length } },
            };

            string json = JsonConvert.SerializeObject(gltf);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            // Паддинг чанков до кратности 4: JSON — пробелами (0x20), BIN — нулями (0x00).
            byte[] jsonPadded = PadTo4(jsonBytes, 0x20);
            byte[] binPadded = PadTo4(binBytes, 0x00);

            int total = 12 + 8 + jsonPadded.Length + 8 + binPadded.Length;

            var outMs = new MemoryStream();
            var ow = new BinaryWriter(outMs);
            ow.Write(0x46546C67);            // "glTF"
            ow.Write(2);                     // version
            ow.Write(total);                 // total length
            ow.Write(jsonPadded.Length);
            ow.Write(0x4E4F534A);            // "JSON"
            ow.Write(jsonPadded);
            ow.Write(binPadded.Length);
            ow.Write(0x004E4942);            // "BIN\0"
            ow.Write(binPadded);
            ow.Flush();
            return outMs.ToArray();
        }

        private static byte[] PadTo4(byte[] data, byte pad)
        {
            int rem = data.Length % 4;
            if (rem == 0) return data;
            var padded = new byte[data.Length + (4 - rem)];
            Array.Copy(data, padded, data.Length);
            for (int i = data.Length; i < padded.Length; i++) padded[i] = pad;
            return padded;
        }

        private static void ComputeMinMax(List<float> pos, out float[] min, out float[] max)
        {
            min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < pos.Count; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    float v = pos[i + k];
                    if (v < min[k]) min[k] = v;
                    if (v > max[k]) max[k] = v;
                }
            }
        }
    }
}
