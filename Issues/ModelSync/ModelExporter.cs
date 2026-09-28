// Перенесено из monumenthunny-dev/tnovpro-issues-revit (Revit/GeometryExporter.cs) для «Модели» TNovPRO:
// вопрос о модели на сайте + синхронизация с центральной моделью (2026-09-28).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using TNovCommon;
using Newtonsoft.Json;

namespace TNovUtils.Issues.ModelSync
{
    /// <summary>
    /// Выгрузка модели в бинарный glTF (.glb) с паспортами элементов — для «Модели»
    /// TNovPRO (вопрос о модели на сайте и ответ в 3D). Два режима:
    ///   <see cref="ExportProject"/> — дом целиком (основной документ и связи), один раз;
    ///   <see cref="ExportElements"/> — только изменённые элементы, при каждой синхронизации.
    /// Координаты: Revit (футы, Z-up) → glTF (метры, Y-up). Контракт с сайтом —
    /// doc/BIM-ASK-SYNC.md в репозитории TNovPRO.
    ///
    /// glTF собирается вручную (без SharpGLTF и т.п.), чтобы не тащить в процесс Revit
    /// лишние сборки и не ловить конфликты версий. Геометрия неиндексированная
    /// (3 вершины на треугольник, плоские нормали).
    ///
    /// Фрагмент одного вопроса (элемент и соседи) — не здесь, а в Issues/Revit/GeometryExporter.
    /// </summary>
    public static class ModelExporter
    {
        private const double FeetToMeters = 0.3048;

        public sealed class ExportResult
        {
            /// <summary>
            /// Готовый файл целиком в памяти — только для МАЛЕНЬКИХ выгрузок
            /// (замечание и его окружение, единицы мегабайт).
            ///
            /// 🔴 Дом в память не помещается ПО УСТРОЙСТВУ ФОРМАТА, а не по нехватке
            /// ОЗУ: `MemoryStream` держит один `byte[]`, а у него предел
            /// `int.MaxValue` — 2 ГБ. Выгрузка 07.09 упёрлась в него на
            /// `set_Capacity(Int32)` при 20 свободных гигабайтах. Большие выгрузки
            /// пишутся сразу на диск и приезжают сюда как <see cref="GlbFile"/>.
            /// </summary>
            public byte[] Glb;
            /// <summary>Готовый файл на диске (временный) — для больших выгрузок.</summary>
            public string GlbFile;
            /// <summary>Размер готового файла в байтах — для отчёта, в обоих случаях.</summary>
            public long GlbLength;

            /// <summary>
            /// Положить готовый .glb туда, куда попросил человек. Путь он выбирает
            /// ПОСЛЕ выгрузки (диалог сохранения), поэтому большая модель ждёт его
            /// во временном файле и переезжает, а не пересобирается.
            /// </summary>
            public void SaveTo(string path)
            {
                if (GlbFile != null)
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(GlbFile, path);
                    GlbFile = null;
                    return;
                }
                File.WriteAllBytes(path, Glb ?? new byte[0]);
            }
            public int TriangleCount;
            public int NeighborCount;
            public int PropertyCount; // паспортов в файле (в профиле сметы больше, чем мешей)
            public bool Truncated;   // соседей было больше MaxNeighbors
            public int Skipped;      // элементы, отсеянные профилем (арматура и т.п.)
            public bool Cancelled;   // выгрузку остановил человек — модель неполная

            // Время по этапам. «Долго» без разбивки — это всегда спор вслепую:
            // 04.08 из «20 минут» 18 оказались открытием серверной модели, а не
            // выгрузкой. Пусть отчёт сам говорит, ГДЕ время: обход базы Revit,
            // сбор паспортов или сборка .glb.
            public double ScanSeconds;   // обход FilteredElementCollector
            public double PassSeconds;   // паспорта (+ геометрия в остальных профилях)
            public double BuildSeconds;  // сборка .glb и сериализация JSON
        }

        /// <summary>
        /// Что делаем с моделью при выгрузке «всё здание».
        ///
        /// <see cref="Full"/> — всё как есть на Fine: просмотр замечаний, где важна
        /// настоящая форма элемента. Смете он больше не нужен — у неё свой профиль
        /// <see cref="Estimate"/>, где геометрия дешёвая, но не теряются элементы.
        ///
        /// <see cref="Walk"/> — для обхода (TNovWalk): человек видит только то, что
        /// снаружи бетона и труб, поэтому детализация раздаётся ПО КАТЕГОРИЯМ, а
        /// арматура не выгружается вовсе. Замер на 76-СУЗДАЛ показал, куда уходит
        /// вес: у КЖ 98,2% треугольников — «Несущая арматура» (она внутри бетона и
        /// не видна ни секунды), у ВК 85% — фитинги и арматура трубопроводов
        /// (деталька 10 см ценой до 4 200 треугольников на Fine), у АР 73% — те же
        /// арматура и сантехприборы.
        /// </summary>
        public enum ExportProfile
        {
            Full = 0,
            Walk = 1,
            /// <summary>
            /// Выгрузка ДЛЯ СМЕТЫ (Афина). Смета считается по ПАСПОРТУ элемента
            /// (объём, площадь, длина, марка), а геометрия нужна только чтобы
            /// человек увидел модель — поэтому здесь всё берётся на Coarse и,
            /// главное, НЕ применяются геометрические фильтры вьювера:
            ///  - отсев «плоской графики» тоньше 3 мм выбрасывал светильники,
            ///    выключатели и розетки — а они считаются ШТУКАМИ, и в ЭЛ_С1
            ///    «Осветительных приборов» не оказалось вовсе;
            ///  - элемент без BoundingBox тоже уходил молча, хотя паспорт у него
            ///    есть и в смету он обязан попасть;
            ///  - арматура, наоборот, нужна целиком: Афина считает её по объёму
            ///    стали (в профиле обхода она не выгружается вовсе).
            /// Элемент без триангуляции всё равно отдаёт паспорт — смете этого
            /// достаточно, «пустых» позиций не появляется.
            /// </summary>
            Estimate = 2,
            /// <summary>
            /// Выгрузка для «Модели» TNovPRO: вопрос о модели на сайте и ответ в 3D.
            /// Паспорт — у КАЖДОГО элемента, как в смете: сайт считает «сколько
            /// дверей», и потерянный элемент — это неверный ответ, а сверочные
            /// цифры синхронизации считают все элементы подряд. Геометрия — с
            /// детализацией обхода; арматура и плоская графика едут паспортом без
            /// геометрии (в 3D её не видно, а весит она больше всего дома).
            /// См. CollectForAsk.
            /// </summary>
            Ask = 3,
        }

        // Категории, которые в профиле «обход» не выгружаются: арматура целиком.
        private static readonly HashSet<int> WalkSkipCategories = new HashSet<int>
        {
            (int)BuiltInCategory.OST_Rebar,                 // несущая арматура
            (int)BuiltInCategory.OST_AreaRein,              // арматура по площади
            (int)BuiltInCategory.OST_PathRein,              // арматура по траектории
            (int)BuiltInCategory.OST_FabricAreas,           // арматурные сетки (область)
            (int)BuiltInCategory.OST_FabricReinforcement,   // арматурные сетки
            (int)BuiltInCategory.OST_Coupler,               // муфты арматуры
        };

        // Ради чего вообще нужен Fine: полотно двери и переплёт окна. На Medium
        // дверные семейства отдают вместо полотна ящик зоны открывания.
        private static readonly HashSet<int> WalkFineCategories = new HashSet<int>
        {
            (int)BuiltInCategory.OST_Doors,
            (int)BuiltInCategory.OST_Windows,
        };

        // На Coarse остаётся только ОБОЛОЧКА вокруг трубы и воздуховода.
        //
        // 🔴 Прежде сюда попадала вся мелочь инженерии скопом, и это стоило вида:
        // кран, унитаз, решётка вентиляции и выключатель на Coarse превращаются в
        // коробки, а человек в обходе смотрит на них в упор — именно они и есть
        // «инженерка вблизи», на которую жаловался Виктор (07.09).
        //
        // Считано по спутнику 76-СУЗДАЛ (133 826 элементов), прежде чем менять:
        //   изоляция труб        26 693  ← остаётся Coarse, это 20% всего дома
        //   изоляция воздуховодов 1 075  ← остаётся Coarse
        //   арматура трубопроводов 1 277 ┐
        //   сантехприборы            591 │
        //   арматура воздуховодов    229 ├ поднимаются до Medium: 2 317 штук,
        //   воздухораспределители    177 │  меньше 2% дома
        //   выключатели               43 ┘
        //
        // Изоляция и остаётся грубой: это трубка вокруг трубы, её форма не читается
        // ни с какого расстояния, а элементов в ней больше, чем во всём остальном
        // списке в двенадцать раз. Поднять её значило бы утяжелить дом ради того,
        // чего никто не увидит.
        private static readonly HashSet<int> WalkCoarseCategories = new HashSet<int>
        {
            (int)BuiltInCategory.OST_PipeInsulations,
            (int)BuiltInCategory.OST_DuctInsulations,
        };

        // Фитинги (OST_PipeFitting/OST_DuctFitting/OST_CableTrayFitting/
        // OST_ConduitFitting — отводы, тройники, муфты, короба лотков) НАРОЧНО
        // не в WalkCoarseCategories. На Coarse они не просто гранёные, а
        // натурально РАЗВАЛИВАЮТСЯ: живой прогон 76-СУЗДАЛ показал обрубки,
        // повисшие в воздухе рядом с трубой вместо аккуратного стыка (поймано
        // глазами). В отличие от арматуры (она всегда круглая, Coarse её не
        // портит), у фитинга форма имеет смысл — колено должно визуально
        // соединять две трубы под углом. Ниже по DetailFor они падают в Medium
        // по умолчанию — недорого: на дом это около 1000 элементов из 148 000.

        /// <summary>
        /// Стоит ли поднимать элемент до Fine, если беднее он тела не дал.
        ///
        /// Мерка — самая длинная сторона габарита: 30 см. Выше неё светильник,
        /// решётка, кран, смеситель — их в обходе видно и форма читается. Ниже —
        /// розетка, выключатель, коробка: их и раньше в доме не было (тела на
        /// своей детализации они не давали и уходили молча), а Fine на пятнадцать
        /// тысяч таких удваивает вес всего дома.
        /// </summary>
        private static bool WorthFine(BoundingBoxXYZ box)
        {
            if (box == null) return false;
            const double FineMinFeet = 0.3 / 0.3048;   // 30 см в футах — внутренних единицах Revit
            double dx = Math.Abs(box.Max.X - box.Min.X);
            double dy = Math.Abs(box.Max.Y - box.Min.Y);
            double dz = Math.Abs(box.Max.Z - box.Min.Z);
            return Math.Max(dx, Math.Max(dy, dz)) >= FineMinFeet;
        }

        /// <summary>
        /// Какой детализацией брать элемент — или null, если он не нужен вовсе.
        /// </summary>
        private static ViewDetailLevel? DetailFor(Category category, ExportProfile profile)
        {
            if (profile == ExportProfile.Full) return ViewDetailLevel.Fine;
            // Смете форма не нужна вовсе: объёмы и длины она берёт из паспорта,
            // а не из треугольников. Coarse — самая дешёвая геометрия, и ничего
            // не отсеивает: категории здесь не исключаются, включая арматуру.
            if (profile == ExportProfile.Estimate) return ViewDetailLevel.Coarse;
            int id = (int)(category?.Id?.LongValue() ?? 0);
            if (WalkSkipCategories.Contains(id)) return null;
            if (WalkFineCategories.Contains(id)) return ViewDetailLevel.Fine;
            if (WalkCoarseCategories.Contains(id)) return ViewDetailLevel.Coarse;
            return ViewDetailLevel.Medium;
        }

        /// <summary>
        /// Модель ПРОЕКТА одним файлом: основной документ и все связанные разделы,
        /// каждый на своём месте. Это то, что нужно Атласу — один .glb, который он
        /// открывает без сборки на стороне игры.
        ///
        /// Разделы приезжают в систему координат ОСНОВНОГО документа: их положение
        /// знает только связь (RevitLinkInstance), сами файлы про своё место в
        /// проекте не знают ничего.
        /// </summary>
        /// <param name="onlyLinks">
        /// Экземпляры связей, отмеченные человеком в окне выбора (см.
        /// UI.LinkPickerWindow). null — прежнее поведение: берём все загруженные.
        /// </param>
        public static ExportResult ExportProject(Document host, int maxElements, ExportProfile profile,
                                                 Action<string> log = null, ExportProgress progress = null,
                                                 ICollection<ElementId> onlyLinks = null)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            // Параметры типа разбираются один раз на тип, а не на каждый экземпляр.
            PropertyCollector.ResetTypeCache();

            var meshes = new List<RoleMesh>();
            var properties = new Dictionary<string, object>();
            XYZ center = null;
            int truncatedCount = 0, skippedTotal = 0;
            bool cancelled = false;
            // Разделы для синхронизации: имя документа (по нему сайт узнаёт раздел,
            // приславший изменения) и положение связи (приращение приходит в
            // координатах своего документа, а сайту нужен дом).
            var sections = new List<KeyValuePair<string, Placed>>();

            void Collect(Document doc, string section, Transform place)
            {
                // Время по КАЖДОМУ разделу пишем в отчёт: без этого нельзя ответить
                // на вопрос «почему так долго» — а он возникает первым.
                var clock = Stopwatch.StartNew();
                var part = profile == ExportProfile.Ask
                    ? CollectForAsk(doc, place, section, ref center, meshes, properties, progress, log)
                    : CollectDocument(doc, null, maxElements, profile, place, section,
                                      ref center, meshes, properties, progress, log);
                sections.Add(new KeyValuePair<string, Placed>(section, new Placed { Document = DocumentName(doc), Place = place }));
                clock.Stop();
                skippedTotal += part.Skipped;
                if (part.Truncated) ++truncatedCount;
                if (part.Cancelled) cancelled = true;
                log?.Invoke($"{section}: элементов {part.NeighborCount:N0}, треугольников {part.TriangleCount:N0}" +
                            (part.Skipped > 0 ? $", отсеяно {part.Skipped:N0}" : "") +
                            $", {clock.Elapsed.TotalSeconds:N0} с" +
                            (part.Truncated ? " (ОБРЕЗАНО по лимиту)" : "") +
                            (part.Cancelled ? " (ПРЕРВАНО)" : ""));
            }

            // Имя без хвоста «_пользователь» локальной копии: иначе раздел основного
            // файла назывался бы по-своему у каждого, кто выгружает.
            Collect(host, SectionOf(DocumentName(host)), null);

            // Точку отсчёта пишем в отчёт числами: две выгрузки одного дома можно
            // сравнить строкой из лога и сразу увидеть, переехала модель или нет.
            // Она же лежит в самом .glb (extras.tnovpro.originGltf).
            if (center != null)
            {
                var origin = ToGltf(center, XYZ.Zero);
                log?.Invoke($"точка отсчёта (glTF, м): {origin[0]:N3}, {origin[1]:N3}, {origin[2]:N3}" +
                            " — она же в файле, extras.tnovpro.originGltf");
            }

            // Список связей собираем ЗАРАНЕЕ: человеку у прогресса нужно понимать,
            // сколько разделов впереди, а не смотреть на счётчик без знаменателя.
            // Состав берём из ModelLinks — оттуда же, откуда его показывает окно
            // выбора: иначе окно и выгрузка однажды разойдутся в том, что считать
            // связью, и человек отметит одно, а уедет другое.
            var chosen = onlyLinks == null
                ? null
                : new HashSet<long>(onlyLinks.Where(id => id != null).Select(id => id.LongValue()));

            var links = new List<ModelLinks.Link>();
            var notChosen = new List<ModelLinks.Link>();
            var notLoaded = new List<ModelLinks.Link>();
            foreach (var link in ModelLinks.Collect(host))
            {
                if (chosen != null && !chosen.Contains(link.InstanceId.LongValue())) { notChosen.Add(link); continue; }
                if (!link.Loaded) { notLoaded.Add(link); continue; }
                links.Add(link);
            }

            log?.Invoke($"связанных разделов: {links.Count}"
                        + (notLoaded.Count > 0 ? $", не загружено: {notLoaded.Count}" : "")
                        + (notChosen.Count > 0 ? $", не выбрано: {notChosen.Count}" : ""));
            foreach (var link in notChosen)
                log?.Invoke($"раздел не выбран, пропускаю: {link.Section} ({link.FileName})");

            for (int i = 0; i < links.Count; i++)
            {
                if (progress != null && progress.IsCancelled) { cancelled = true; break; }
                var li = host.GetElement(links[i].InstanceId) as RevitLinkInstance;
                Document ld = null;
                try { ld = li?.GetLinkDocument(); } catch { }
                if (ld == null) continue;
                var section = SectionOf(DocumentName(ld));
                progress?.Report($"раздел {i + 1} из {links.Count}: {section}");
                Transform place = null;
                try { place = li.GetTotalTransform(); } catch { }
                try { Collect(ld, section, place); }
                catch (Exception ex) { log?.Invoke($"{ld.Title}: ОШИБКА — {ex.Message}"); }
            }

            // 🔴 Незагруженные называем ПОИМЁННО. Прежняя строка «не загружено: 3»
            // не говорила, чего именно нет, — и три выгрузки подряд уезжали без
            // раздела СС, а заметили это только через две недели.
            foreach (var link in notLoaded)
                log?.Invoke($"⚠ связь не загружена, в модель НЕ ПОПАЛА: {link.FileName} — {link.Status}");

            if (meshes.Count == 0)
                throw new InvalidOperationException("Не удалось извлечь ни одного элемента с геометрией.");

            progress?.Report($"сборка файла: {meshes.Count:N0} элементов");
            var build = Stopwatch.StartNew();
            Dictionary<string, object> extra = null;
            if (profile == ExportProfile.Ask && center != null)
            {
                var declared = new Dictionary<string, object>();
                foreach (var s in sections)
                {
                    if (declared.ContainsKey(s.Key)) continue;
                    declared[s.Key] = new Dictionary<string, object>
                    {
                        ["document"] = s.Value.Document,
                        ["matrix"] = SectionMatrix(s.Value.Place, center),
                    };
                    log?.Invoke($"раздел {s.Key}: документ «{s.Value.Document}»");
                }
                extra = new Dictionary<string, object> { ["sections"] = declared };
            }
            var glbFile = BuildGlbFile(meshes, properties, center, out long glbLength, extra);
            build.Stop();
            log?.Invoke($"сборка .glb: {build.Elapsed.TotalSeconds:N0} с, {glbLength / 1024.0 / 1024:N1} МБ");

            return new ExportResult
            {
                GlbFile = glbFile,
                GlbLength = glbLength,
                TriangleCount = meshes.Sum(m => m.Positions.Count / 9),
                NeighborCount = meshes.Count,
                Truncated = truncatedCount > 0,
                Skipped = skippedTotal,
                Cancelled = cancelled,
            };
        }

        /// <summary>Раздел из имени файла: «76-СУЗДАЛ.23_ОВ_С1» → «ОВ_С1».</summary>
        private static string SectionOf(string title)
        {
            if (string.IsNullOrEmpty(title)) return "модель";
            var parts = title.Split('_');
            if (parts.Length <= 1) return title;
            // Отбрасываем шифр объекта (первую часть), остальное — раздел с секцией.
            return string.Join("_", parts, 1, parts.Length - 1);
        }

        // ── «Модель» TNovPRO: дом целиком, приращения, сверка ────────────────

        private sealed class Placed
        {
            public string Document;
            public Transform Place;
        }

        /// <summary>
        /// Имя документа, по которому сайт узнаёт раздел, приславший синхронизацию.
        /// Одно правило и для полной выгрузки (связь в сводном файле), и для
        /// приращения (локальная копия у инженера): заголовок без «.rvt» и без
        /// хвоста «_пользователь», который Revit добавляет локальной копии.
        /// Разойдутся правила — синхронизации тихо перестанут находить свой раздел.
        /// </summary>
        public static string DocumentName(Document doc)
        {
            var title = (doc?.Title ?? "").Replace(",", " ");
            if (title.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) title = title.Substring(0, title.Length - 4);
            string user = null;
            try { user = doc?.Application?.Username; } catch { }
            if (!string.IsNullOrEmpty(user)) title = title.Replace("_" + user.Replace(",", ""), "");
            return title.Trim();
        }

        /// <summary>
        /// Матрица (glTF, column-major) из координат ДОКУМЕНТА раздела в координаты
        /// выгрузки дома: приращение приходит без связи и без общего центра, а
        /// сайту его надо поставить на место. d = G(p) → G(place(p)) − G(center),
        /// где G — футы/Z-вверх → метры/Y-вверх.
        /// </summary>
        private static double[] SectionMatrix(Transform place, XYZ center)
        {
            var P = place ?? Transform.Identity;
            // Вектор glTF → вектор Revit (футы): (a, b, c) → (a, −c, b) / k.
            XYZ FromGltf(double a, double b, double c) => new XYZ(a / FeetToMeters, -c / FeetToMeters, b / FeetToMeters);
            double[] ToGltfVec(XYZ v) => new[] { v.X * FeetToMeters, v.Z * FeetToMeters, -v.Y * FeetToMeters };
            var cols = new[]
            {
                ToGltfVec(P.OfVector(FromGltf(1, 0, 0))),
                ToGltfVec(P.OfVector(FromGltf(0, 1, 0))),
                ToGltfVec(P.OfVector(FromGltf(0, 0, 1))),
            };
            var t = ToGltf(P.OfPoint(XYZ.Zero), center);
            return new[]
            {
                cols[0][0], cols[0][1], cols[0][2], 0,
                cols[1][0], cols[1][1], cols[1][2], 0,
                cols[2][0], cols[2][1], cols[2][2], 0,
                t[0], t[1], t[2], 1,
            };
        }

        /// <summary>
        /// Элемент идёт в «Модель»: модельная категория, не площадка/легенда/камера.
        /// <paramref name="detail"/> — детализация геометрии; null — только паспорт
        /// (арматура). ОДНО правило для полной выгрузки, приращения и сверки —
        /// иначе сверочные цифры разойдутся с сайтом на пустом месте.
        /// </summary>
        private static bool IsAskCandidate(Element el, out ViewDetailLevel? detail)
        {
            detail = null;
            var cat = el?.Category;
            if (cat == null || cat.CategoryType != CategoryType.Model) return false;
            if (IsExcludedFromModel(cat)) return false;
            if (el is ElementType) return false;
            detail = DetailFor(cat, ExportProfile.Walk);
            return true;
        }

        /// <summary>
        /// Геометрия элемента с «лестницей» детализации (как в CollectDocument):
        /// не дал тела на своей — берём богаче, до Fine только крупное.
        /// </summary>
        private static RoleMesh TryMesh(Candidate c, long id, string section, XYZ center,
                                        HashSet<ElementId> voidMaterials, Transform toShared)
        {
            var rm = new RoleMesh("model", id) { Section = section };
            if (AddElement(c.El, rm, center, c.Detail, voidMaterials, toShared)) return rm;
            if (c.Detail == ViewDetailLevel.Fine) return null;
            var richer = c.Detail == ViewDetailLevel.Coarse
                ? new[] { ViewDetailLevel.Medium, ViewDetailLevel.Fine }
                : new[] { ViewDetailLevel.Fine };
            foreach (var level in richer)
            {
                if (level == ViewDetailLevel.Fine && !WorthFine(c.Box)) break;
                rm = new RoleMesh("model", id) { Section = section };
                if (AddElement(c.El, rm, center, level, voidMaterials, toShared)) return rm;
            }
            return null;
        }

        /// <summary>Кандидат с габаритом; плоская графика и элементы без габарита — только паспорт.</summary>
        private static Candidate AskCandidate(Element el, ViewDetailLevel? detail)
        {
            var c = new Candidate { El = el, Detail = detail ?? ViewDetailLevel.Medium, NoGeometry = detail == null };
            if (c.NoGeometry) return c;
            var bb = el.get_BoundingBox(null);
            if (bb == null || IsFlatBox(bb)) { c.NoGeometry = true; return c; }
            c.Box = bb;
            c.Center = (bb.Min + bb.Max) * 0.5;
            return c;
        }

        /// <summary>
        /// Раздел для «Модели»: паспорт у каждого элемента, геометрия — где она есть.
        /// Ничего не выбрасывается молча: не дал тела — едет паспортом.
        /// </summary>
        private static ExportResult CollectForAsk(Document doc, Transform place, string section,
            ref XYZ center, List<RoleMesh> meshList, Dictionary<string, object> properties,
            ExportProgress progress, Action<string> log)
        {
            var label = string.IsNullOrEmpty(section) ? (doc.Title ?? "модель") : section;
            var toShared = place ?? Transform.Identity;
            var scan = Stopwatch.StartNew();
            var picked = new List<Candidate>();
            int seen = 0;
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if ((++seen & 4095) == 0)
                {
                    if (progress != null && progress.IsCancelled) break;
                    progress?.Report($"{label}: поиск элементов — найдено {picked.Count:N0}");
                }
                if (!IsAskCandidate(el, out var detail)) continue;
                picked.Add(new Candidate { El = el, Detail = detail ?? ViewDetailLevel.Medium, NoGeometry = detail == null });
            }
            for (int i = 0; i < picked.Count; i++)
            {
                if ((i & 511) == 0)
                {
                    if (progress != null && progress.IsCancelled) break;
                    progress?.Report($"{label}: габариты", i, picked.Count);
                }
                picked[i] = AskCandidate(picked[i].El, picked[i].NoGeometry ? (ViewDetailLevel?)null : picked[i].Detail);
            }
            scan.Stop();

            // Точка отсчёта — середина «ядра» первого раздела, без улетевших
            // элементов (детали узлов за 1,7 км): те едут в модель, но центр не тянут.
            if (center == null)
            {
                var boxed = picked.Where(c => c.Box != null).ToList();
                var core = boxed.Count > 0 ? DropOutliers(boxed, OutlierRadiusFeet) : boxed;
                var union = UnionBox(core);
                center = union == null ? toShared.OfPoint(XYZ.Zero) : toShared.OfPoint((union.Min + union.Max) * 0.5);
            }

            var voids = VoidMaterials(doc);
            var first = meshList.Count;
            int noMesh = 0;
            bool cancelled = false;
            var pass = Stopwatch.StartNew();
            for (int i = 0; i < picked.Count; i++)
            {
                if ((i & 127) == 0)
                {
                    if (progress != null && progress.IsCancelled) { cancelled = true; break; }
                    progress?.Report($"{label}: геометрия и паспорта", i, picked.Count);
                }
                var c = picked[i];
                long id = c.El.Id.LongValue();
                if (!c.NoGeometry)
                {
                    var rm = TryMesh(c, id, section, center, voids, toShared);
                    if (rm != null) meshList.Add(rm); else ++noMesh;
                }
                AddProperties(properties, doc, c.El, id, section);
            }
            pass.Stop();
            var added = meshList.GetRange(first, meshList.Count - first);
            log?.Invoke($"{label}: паспортов {picked.Count:N0}, с геометрией {added.Count:N0}"
                        + (noMesh > 0 ? $", без тела на любой детализации {noMesh:N0}" : ""));
            return new ExportResult
            {
                TriangleCount = added.Sum(m => m.Positions.Count / 9),
                NeighborCount = added.Count,
                PropertyCount = picked.Count,
                Cancelled = cancelled,
                ScanSeconds = scan.Elapsed.TotalSeconds,
                PassSeconds = pass.Elapsed.TotalSeconds,
            };
        }

        /// <summary>
        /// Приращение для синхронизации: только перечисленные элементы, в координатах
        /// СВОЕГО документа (без связи и без общего центра — на место их ставит сайт
        /// матрицей раздела). Каждый элемент — полное состояние: паспорт и геометрия.
        /// Исчезнувшие с момента правки (добавили и удалили до синхронизации) уходят
        /// в удалённые.
        /// </summary>
        public static byte[] ExportElements(Document doc, ICollection<long> ids, ICollection<long> deleted,
                                            IDictionary<string, int> counts, DateTime syncedAtUtc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            PropertyCollector.ResetTypeCache();
            var meshes = new List<RoleMesh>();
            var properties = new Dictionary<string, object>();
            var gone = new HashSet<long>(deleted ?? new long[0]);
            var voids = VoidMaterials(doc);
            foreach (var id in ids ?? new long[0])
            {
                var el = doc.GetElement(MakeElementId(id));
                if (el == null) { gone.Add(id); continue; }
                if (!IsAskCandidate(el, out var detail)) continue;
                var c = AskCandidate(el, detail);
                if (!c.NoGeometry)
                {
                    var rm = TryMesh(c, id, null, XYZ.Zero, voids, null);
                    if (rm != null) meshes.Add(rm);
                }
                AddProperties(properties, doc, el, id);
            }
            var extra = new Dictionary<string, object>
            {
                ["delta"] = new Dictionary<string, object>
                {
                    ["deleted"] = gone.ToArray(),
                    ["syncedAt"] = syncedAtUtc.ToUniversalTime().ToString("o"),
                    ["counts"] = counts,
                },
            };
            return BuildGlb(meshes, properties, null, extra);
        }

        /// <summary>
        /// Сверочные цифры: сколько элементов каждой категории во ВСЁМ документе —
        /// тем же отбором, что у выгрузки. Без геометрии и паспортов, только обход
        /// базы. Сайт сравнивает их с собой и говорит, если что-то прошло мимо.
        /// </summary>
        public static Dictionary<string, int> CountCategories(Document doc)
        {
            var counts = new Dictionary<string, int>();
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (!IsAskCandidate(el, out _)) continue;
                var name = el.Category.Name ?? "—";
                counts.TryGetValue(name, out int had);
                counts[name] = had + 1;
            }
            return counts;
        }

        /// <summary>
        /// Складывает геометрию ОДНОГО документа в общие списки. Вынесено из
        /// ExportModel, чтобы проект (основной файл + связи) собирался в одну
        /// модель: центр рецентрирования общий на всех, иначе разделы разъедутся.
        /// </summary>
        private static ExportResult CollectDocument(Document doc, string categoryFilter, int maxElements,
            ExportProfile profile, Transform worldTransform, string section,
            ref XYZ center, List<RoleMesh> meshList, Dictionary<string, object> properties,
            ExportProgress progress = null, Action<string> log = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var label = string.IsNullOrEmpty(section) ? (doc.Title ?? "модель") : section;
            bool cancelled = false;

            // 🔴 ПОРЯДОК ОТСЕВА РЕШАЕТ ВРЕМЯ ВЫГРУЗКИ. Категория элемента известна из
            // базы Revit даром, а get_BoundingBox считается ПО ГЕОМЕТРИИ и стоит дорого.
            // Поэтому профиль спрашивается ПЕРВЫМ: в профиле обхода так отсеивается
            // арматура (у КЖ это 98% элементов), и габарит для неё не считается вовсе.
            // Раньше каждый из 75 950 стержней проходил четыре обращения к габариту —
            // в фильтре коллектора, в отсеве плоской графики, в отсеве улетевших и в
            // общем габарите — и только потом выбрасывался по категории.
            int skipped = 0;
            // Профиль сметы: геометрические фильтры вьювера здесь ВРЕДНЫ — они
            // выбрасывают тонкие изделия (светильники, розетки) и элементы без
            // габарита, у которых паспорт есть и в смету они обязаны попасть.
            bool estimate = profile == ExportProfile.Estimate;
            var candidates = new List<Candidate>();
            // Кто и почему не доехал — по категориям. Молчаливая потеря выглядит
            // как норма: «связей не загружено: 3» без имён стоило двух недель без
            // раздела СС, а «Осветительных приборов» в доме нет до сих пор, и
            // причина не названа.
            var dropsNoBox = new Dictionary<string, int>();
            var dropsFlat = new Dictionary<string, int>();
            var dropsNoMesh = new Dictionary<string, int>();
            var rescuedByMedium = new Dictionary<string, int>();
            void Tally(Dictionary<string, int> into, Element element)
            {
                var name = element?.Category?.Name ?? "(без категории)";
                into.TryGetValue(name, out int had);
                into[name] = had + 1;
            }
            // Обход базы Revit на боевом разделе — это сотни тысяч элементов и
            // заметные секунды. Молчать здесь нельзя: окно должно показывать, что
            // работа идёт, ещё до того как станет известно ОБЩЕЕ число элементов.
            int seen = 0;
            var scanClock = Stopwatch.StartNew();
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if ((++seen & 4095) == 0)
                {
                    if (progress != null && progress.IsCancelled) { cancelled = true; break; }
                    progress?.Report($"{label}: поиск элементов — найдено {candidates.Count:N0}");
                }
                var cat = el.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                if (IsExcludedFromModel(cat)) continue;
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    cat.Name.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var detail = DetailFor(cat, profile);
                if (detail == null) { ++skipped; continue; }
                candidates.Add(new Candidate { El = el, Detail = detail.Value });
            }
            scanClock.Stop();

            if (candidates.Count == 0)
                throw new InvalidOperationException("В модели не нашлось элементов с геометрией.");

            // Габарит — ОДИН раз на элемент. Дальше по нему работают все три проверки:
            // плоская 2D-графика, отсев улетевших и общий габарит модели.
            // 🔴 В СМЕТЕ ГАБАРИТ НЕ НУЖЕН ВОВСЕ. Все три проверки геометрические, а
            // профиль сметы ни одной из них не применяет: тонкие изделия и элементы
            // без габарита обязаны попасть в смету, «улетевшие» тоже (их объёмы
            // реальны), общий габарит нужен только для рецентровки вершин. При этом
            // get_BoundingBox считается ПО ГЕОМЕТРИИ и на боевом разделе это сотни
            // тысяч дорогих вызовов — 2026-08-10 убраны, выгрузка для Афины больше
            // не платит за то, чем не пользуется.
            List<Candidate> elements;
            if (estimate)
            {
                elements = candidates;
                progress?.Report($"{label}: паспорта {candidates.Count:N0} элем. (габариты не считаем)");
            }
            else
            {
                elements = new List<Candidate>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                {
                    if ((i & 511) == 0)
                    {
                        if (progress != null && progress.IsCancelled) { cancelled = true; break; }
                        progress?.Report($"{label}: габариты", i, candidates.Count);
                    }
                    var c = candidates[i];
                    var bb = c.El.get_BoundingBox(null);
                    // 🔴 Отсев ЗДЕСЬ молчал, и это стоило дорого: в выгрузке
                    // 76-СУЗДАЛ «Осветительных приборов» нет ВООБЩЕ (0 на весь
                    // дом), выключателей 38 на 17 этажей — а понять, выбросил их
                    // экспортёр или их нет в модели, было нечем. Теперь каждый
                    // отброшенный элемент считается по своей категории и причине,
                    // и отчёт называет их поимённо.
                    if (bb == null)
                    {
                        Tally(dropsNoBox, c.El);
                        continue;                               // элемент без геометрии
                    }
                    if (IsFlatBox(bb))
                    {
                        Tally(dropsFlat, c.El);
                        continue;                               // плоская 2D-графика в модельной категории
                    }
                    c.Box = bb;
                    c.Center = (bb.Min + bb.Max) * 0.5;
                    elements.Add(c);
                }
            }

            if (elements.Count == 0)
                throw new InvalidOperationException("В модели не нашлось элементов с геометрией.");

            // Отброшенное называем поимённо — по категориям, самые многочисленные
            // сверху. Строка в отчёте отвечает на вопрос, из-за которого пришлось
            // разбирать всю выгрузку: «светильников нет — их не выгрузили или их
            // нет в модели?»
            void ReportDrops(Dictionary<string, int> drops, string why)
            {
                if (drops.Count == 0) return;
                int total = 0;
                foreach (var pair in drops) total += pair.Value;
                var top = drops.OrderByDescending(p => p.Value).Take(8)
                    .Select(p => $"{p.Key} {p.Value:N0}");
                log?.Invoke($"{label}: не выгружено {total:N0} ({why}) — {string.Join(", ", top)}"
                            + (drops.Count > 8 ? $" и ещё {drops.Count - 8} категорий" : ""));
            }
            ReportDrops(dropsNoBox, "нет габарита: геометрии у элемента нет вовсе");
            ReportDrops(dropsFlat, "плоская графика тоньше 3 мм");

            // Геометрический отсев «улетевших» элементов. Чёрного списка категорий мало:
            // на боевой АР выбросы дала категория «Элементы узлов» (70 шт. на расстоянии
            // до 1697 м от здания). Отсекаем по расстоянию от МЕДИАНЫ центров — работает
            // на любой модели независимо от того, в какой категории лежит мусор.
            // В смете отсева нет: там нет габаритов, а объёмы «улетевших» реальны.
            if (!estimate) elements = DropOutliers(elements, OutlierRadiusFeet);

            // 🔴 СОВМЕЩЕНИЕ РАЗДЕЛОВ. Каждый раздел — свой документ Revit со своей
            // внутренней системой. У 76-СУЗДАЛ АР и КЖ случайно совпали, а ОВ
            // уехал на 36 м, ВК на 13 — в обходе это выглядело призрачной башней
            // инженерии рядом с домом. Положение раздела знает ТОЛЬКО связь в
            // основном файле (RevitLinkInstance), поэтому его передают снаружи;
            // проектная площадка тут не помогает — у этого проекта она единичная.
            var toShared = worldTransform ?? Transform.Identity;

            // Рецентрируем в центр всей модели — иначе мировые координаты Revit
            // (десятки км от начала) убивают точность float во вьювере. Центр
            // задаёт ПЕРВЫЙ документ; связи считаются относительно него, иначе
            // каждый раздел уехал бы в свой ноль.
            if (center == null)
            {
                var union = UnionBox(elements);
                if (union == null && !estimate) throw new InvalidOperationException("У элементов модели нет BoundingBox.");
                center = union == null
                    ? toShared.OfPoint(XYZ.Zero)     // смета: габаритов нет, но паспорта выгрузить надо
                    : toShared.OfPoint((union.Min + union.Max) * 0.5);

                // 🔴 ТОЧКА ОТСЧЁТА — середина габарита ПЕРВОГО раздела, и её сдвиг
                // двигает в .glb ВЕСЬ дом: 20.08.2026 модель уехала на 115 м по
                // горизонтали и 29 м по высоте только потому, что в АР изменился
                // общий габарит. Найти виновника было нечем — теперь и габарит, и
                // самые дальние от центра элементы пишутся в отчёт: если один
                // элемент стоит вдвое дальше остальных, он и растянул коробку.
                if (union != null && log != null)
                {
                    var mid = (union.Min + union.Max) * 0.5;
                    log($"габарит раздела {label}: " +
                        $"{(union.Max.X - union.Min.X) * FeetToMeters:N1} × " +
                        $"{(union.Max.Y - union.Min.Y) * FeetToMeters:N1} × " +
                        $"{(union.Max.Z - union.Min.Z) * FeetToMeters:N1} м");
                    foreach (var c in elements.Where(c => c.Center != null)
                                              .OrderByDescending(c => c.Center.DistanceTo(mid))
                                              .Take(3))
                    {
                        log($"  дальний элемент: {c.El.Id.LongValue()} · {c.El.Category?.Name} · " +
                            $"{c.El.Name} — {c.Center.DistanceTo(mid) * FeetToMeters:N1} м от центра");
                    }
                }
            }

            // Считаем ЛИМИТ ПО ЭЛЕМЕНТАМ С ГЕОМЕТРИЕЙ, а не по позиции в коллекторе:
            // в модели полно элементов без солидов (помещения, оси, аннотации в
            // модельных категориях) — иначе они «съедали» весь лимит, и в .glb
            // попадала лишь малая часть здания.
            // Детализация. В профиле Full — FINE везде: на Medium дверные семейства
            // отдают вместо полотна ящик зоны открывания, из-за чего в обходе двери
            // «не открывались» и перекрывали проход. В профиле Walk детализация
            // раздаётся по категориям (см. DetailFor): Fine дверям и окнам, Coarse
            // изоляции труб и воздуховодов, Medium остальному, арматура не
            // выгружается вовсе.
            // Материалы-пустоты нужны только триангуляции — в смете её нет.
            var voidMaterials = estimate ? null : VoidMaterials(doc);
            var firstMesh = meshList.Count;
            bool truncated = false;
            var passClock = Stopwatch.StartNew();
            for (int i = 0; i < elements.Count && !cancelled; i++)
            {
                if ((i & 127) == 0)
                {
                    if (progress != null && progress.IsCancelled) { cancelled = true; break; }
                    progress?.Report(estimate ? $"{label}: паспорта" : $"{label}: геометрия", i, elements.Count);
                }
                // Лимит: в обычных профилях считаем выгруженные меши, в смете —
                // элементы (у части из них меша нет вовсе, и они всё равно едут).
                if ((estimate ? i : meshList.Count - firstMesh) >= maxElements) { truncated = true; break; }
                var c = elements[i];
                long id = c.El.Id.LongValue();
                // 🔴 СМЕТЕ ГЕОМЕТРИЯ НЕ НУЖНА СОВСЕМ. Движок читает из .glb только
                // extras.tnovpro.elements (паспорта): объёмы, площади и длины берутся
                // из параметров, а не из мешей. Триангуляция 15,7 млн треугольников
                // была чистой платой за файл, который никто не смотрит, — 2026-08-10
                // в этом профиле она выключена целиком.
                if (!estimate)
                {
                    var rm = new RoleMesh("model", id) { Section = section };
                    bool hasMesh = AddElement(c.El, rm, center, c.Detail, voidMaterials, toShared);
                    // 🔴 САМАЯ ТИХАЯ ИЗ ПОТЕРЬ. Габарит у элемента есть, фильтры он
                    // прошёл, а на своей детализации не отдал ни одного
                    // треугольника — и уходил молча, без паспорта. Так из дома
                    // пропали ВСЕ «Осветительные приборы» (0 на 17 этажей) и почти
                    // все выключатели: многие семейства на грубой детализации не
                    // дают тела вовсе.
                    //
                    // Поднимаемся ПО СТУПЕНЯМ, пока тело не появится, а не на одну
                    // ступень: с 07.09 мелочь инженерии начинает уже с Medium, и
                    // прежняя починка «Coarse → Medium» для неё не сработала бы —
                    // семейство, молчащее на Medium, снова ушло бы в тишину.
                    //
                    // 🔴 До Fine поднимаем ТОЛЬКО КРУПНОЕ, и это цена, измеренная
                    // в бою. Первый прогон 07.09: 15 568 «Электрических приборов»
                    // — розетки и выключатели по 8 см — тела не дают ни на Coarse,
                    // ни на Medium, ушли на Fine и разом дали разделу ЭЛ 28 975 010
                    // треугольников: вдвое больше, чем весь дом до того (14,5 млн).
                    // Файл после этого не собрался вовсе. Розетку в обходе никто не
                    // разглядывает; крупное (светильник, решётка, кран) — да.
                    if (!hasMesh && c.Detail != ViewDetailLevel.Fine)
                    {
                        var richer = c.Detail == ViewDetailLevel.Coarse
                            ? new[] { ViewDetailLevel.Medium, ViewDetailLevel.Fine }
                            : new[] { ViewDetailLevel.Fine };
                        foreach (var level in richer)
                        {
                            if (level == ViewDetailLevel.Fine && !WorthFine(c.Box)) break;
                            rm = new RoleMesh("model", id) { Section = section };
                            hasMesh = AddElement(c.El, rm, center, level, voidMaterials, toShared);
                            if (hasMesh) { Tally(rescuedByMedium, c.El); break; }
                        }
                    }
                    if (!hasMesh) { Tally(dropsNoMesh, c.El); continue; }
                    meshList.Add(rm);
                }
                AddProperties(properties, doc, c.El, id, section, estimate);
            }
            passClock.Stop();
            ReportDrops(dropsNoMesh, "габарит есть, а треугольников нет ни на одной детализации");
            ReportDrops(rescuedByMedium, "не дали тела на своей детализации — взяты богаче");
            if (meshList.Count == firstMesh && properties.Count == 0 && !cancelled)
                throw new InvalidOperationException("Не удалось извлечь триангулированную геометрию модели.");

            var added = meshList.GetRange(firstMesh, meshList.Count - firstMesh);
            return new ExportResult
            {
                TriangleCount = added.Sum(m => m.Positions.Count / 9),
                NeighborCount = added.Count,
                Truncated = truncated,
                Skipped = skipped,
                Cancelled = cancelled,
                ScanSeconds = scanClock.Elapsed.TotalSeconds,
                PassSeconds = passClock.Elapsed.TotalSeconds,
            };
        }

        /// <summary>
        /// Элемент, отобранный к выгрузке: сам элемент, с какой детализацией его
        /// брать и его габарит. Габарит считается один раз и переиспользуется —
        /// это самая дорогая часть подготовки.
        /// </summary>
        private sealed class Candidate
        {
            public Element El;
            public ViewDetailLevel Detail;
            public BoundingBoxXYZ Box;
            public XYZ Center;
            /// <summary>«Модель»: элемент едет только паспортом (арматура, плоская графика).</summary>
            public bool NoGeometry;
        }

        // Категории, которые НЕ являются зданием и только мешают во вьювере:
        // компоненты легенд лежат в координатах видов-легенд (на боевой АР — за 1.7 км
        // от здания, из-за чего камера уезжала и здание становилось точкой), а
        // площадка/топография/дороги/участок растягивают модель на всю площадку.
        // Для замечаний (Export) фильтр НЕ применяется — там важно окружение как есть.
        private static readonly HashSet<int> ExcludedCategories = new HashSet<int>
        {
            (int)BuiltInCategory.OST_LegendComponents,   // компоненты легенды
            (int)BuiltInCategory.OST_Site,               // площадка
            (int)BuiltInCategory.OST_Topography,         // топография
            (int)BuiltInCategory.OST_BuildingPad,        // основание здания
            (int)BuiltInCategory.OST_Roads,              // дороги
            (int)BuiltInCategory.OST_Parking,            // парковка
            (int)BuiltInCategory.OST_Planting,           // растения
            (int)BuiltInCategory.OST_Entourage,          // антураж (люди/машины)
            (int)BuiltInCategory.OST_SitePropertyLineSegment, // линии участка
            (int)BuiltInCategory.OST_SiteProperty,       // участок
            // 🔴 «Камеры» — это сохранённые 3D-виды, а не предмет. Треугольников
            // они не дают, но габарит у них есть, и стоят они где угодно: у
            // 76-СУЗДАЛ три вида ({3D - shatohin.v}, две копии {3D - poryvaev.i})
            // отнесло на 170–190 м, растянув габарит АР до 283 × 297 × 487 м при
            // доме 45 × 63 × 69. Точка рецентрирования — середина этого габарита,
            // поэтому чужой сохранённый вид двигал В МОДЕЛИ ВЕСЬ ДОМ: 20.08.2026
            // так уехало на 115,66 м по горизонтали и 28,92 м по высоте.
            (int)BuiltInCategory.OST_Cameras,            // 3D-виды (не предмет)
        };

        // Радиус «здания + ближайшая площадка» от медианы центров: всё дальше — мусор
        // (детали узлов, элементы в координатах площадки, забытые копии). 1000 футов ≈ 300 м.
        private const double OutlierRadiusFeet = 1000.0;

        /// <summary>
        /// Плоская 2D-графика в модельных категориях: цветовые области, заливки,
        /// «Сплошной_Белый» и т.п. — bbox с нулевой толщиной по одной из осей.
        /// На боевой АР это давало во вьювере огромные белые «полосы» 130 x 0 x 14 м
        /// поперёк здания. Настоящие конструкции всегда имеют объём.
        /// </summary>
        private static bool IsFlatBox(BoundingBoxXYZ bb)
        {
            if (bb == null) return true;
            const double MinThicknessFeet = 0.01;   // ~3 мм
            return (bb.Max.X - bb.Min.X) < MinThicknessFeet
                || (bb.Max.Y - bb.Min.Y) < MinThicknessFeet
                || (bb.Max.Z - bb.Min.Z) < MinThicknessFeet;
        }

        private static List<Candidate> DropOutliers(List<Candidate> elements, double radiusFeet)
        {
            double Median(Func<XYZ, double> pick)
            {
                // в профиле сметы у части элементов габарита нет вовсе — их центр
                // неизвестен, и в медиану они не идут (но и не выбрасываются)
                var vals = elements.Where(c => c.Center != null).Select(c => pick(c.Center)).OrderBy(v => v).ToList();
                return vals.Count == 0 ? 0 : vals[vals.Count / 2];
            }
            var median = new XYZ(Median(c => c.X), Median(c => c.Y), Median(c => c.Z));

            var kept = new List<Candidate>(elements.Count);
            foreach (var c in elements)
                if (c.Center == null || c.Center.DistanceTo(median) <= radiusFeet) kept.Add(c);

            // Подстраховка: если фильтр «съел» почти всё (странная геометрия модели) —
            // лучше показать всё, чем пустую сцену.
            return kept.Count >= elements.Count / 2 ? kept : elements;
        }

        private static bool IsExcludedFromModel(Category category)
        {
            if (category == null) return true;
            if (ExcludedCategories.Contains(category.Id.IntValue())) return true;
            // Подстраховка на случай локализованных/пользовательских категорий,
            // не покрытых BuiltInCategory.
            var name = category.Name ?? "";
            return name.IndexOf("легенд", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("топограф", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Переносимо 2022–2024: int-конструктор ElementId.
        private static ElementId MakeElementId(long value) => ElementIdCompat.ToElementId(value);

        /// <summary>Паспорт элемента (категория/семейство/тип/уровень/материалы/параметры) в общий словарь.</summary>
        private static void AddProperties(Dictionary<string, object> into, Document doc, Element el, long id,
                                          string section = null, bool forEstimate = false)
        {
            if (el == null) return;
            var key = string.IsNullOrEmpty(section) ? id.ToString() : section + ":" + id;
            if (into.ContainsKey(key)) return;
            var info = PropertyCollector.Collect(doc, el, forEstimate);
            if (info == null) return;
            // Раздел кладём в сам паспорт: по нему читающая сторона раскладывает
            // модель на дисциплины (рентген инженерии) без гадания по категориям.
            if (!string.IsNullOrEmpty(section) && info is Dictionary<string, object> map)
            {
                map["tnovSection"] = section;
            }
            into[key] = info;
        }

        /// <summary>Общий габарит по уже посчитанным габаритам отобранных элементов.</summary>
        private static BoundingBoxXYZ UnionBox(IEnumerable<Candidate> picked)
        {
            double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
            double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
            bool any = false;
            foreach (var c in picked)
            {
                var bb = c.Box;
                if (bb == null) continue;
                any = true;
                minx = Math.Min(minx, bb.Min.X); miny = Math.Min(miny, bb.Min.Y); minz = Math.Min(minz, bb.Min.Z);
                maxx = Math.Max(maxx, bb.Max.X); maxy = Math.Max(maxy, bb.Max.Y); maxz = Math.Max(maxz, bb.Max.Z);
            }
            if (!any) return null;
            return new BoundingBoxXYZ { Min = new XYZ(minx, miny, minz), Max = new XYZ(maxx, maxy, maxz) };
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

        /// <summary>
        /// Материалы-«пустоты»: в семействах Revit ими залиты технические тела —
        /// прежде всего ЗОНА ОТКРЫВАНИЯ двери («N Воздух»). В модели их не видно,
        /// а в выгрузке это обычный ящик: он перекрывает проход и мешает понять,
        /// где полотно. Собираем один раз на документ и отсекаем по граням.
        /// </summary>
        private static HashSet<ElementId> VoidMaterials(Document doc)
        {
            var set = new HashSet<ElementId>();
            if (doc == null) return set;
            try
            {
                foreach (var m in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
                {
                    var name = m.Name ?? string.Empty;
                    if (name.IndexOf("воздух", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("air", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        set.Add(m.Id);
                    }
                }
            }
            catch { /* нет доступа к материалам — просто ничего не отсекаем */ }
            return set;
        }

        /// <summary>Триангулирует элемент и складывает плоско-затенённые треугольники в RoleMesh. true — добавил хоть что-то.</summary>
        /// <param name="toShared">
        /// Перевод из внутренних координат ДОКУМЕНТА в общие координаты площадки.
        /// Без него разделы проекта не совмещаются: у каждого .rvt своя внутренняя
        /// система. null — оставить как есть (просмотр одного замечания, там
        /// совмещать не с чем).
        /// </param>
        private static bool AddElement(Element el, RoleMesh mesh, XYZ center, ViewDetailLevel detail,
                                       HashSet<ElementId> voidMaterials = null, Transform toShared = null)
        {
            if (el == null) return false;
            var opt = new Options { ComputeReferences = false, DetailLevel = detail };
            GeometryElement ge;
            try { ge = el.get_Geometry(opt); }
            catch { return false; }
            if (ge == null) return false;

            int before = mesh.Positions.Count;
            CollectTriangles(ge, voidMaterials, (a, b, c) =>
            {
                if (toShared != null)
                {
                    a = toShared.OfPoint(a);
                    b = toShared.OfPoint(b);
                    c = toShared.OfPoint(c);
                }
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

        private static void CollectTriangles(GeometryElement ge, HashSet<ElementId> voidMaterials,
                                             Action<XYZ, XYZ, XYZ> emit)
        {
            bool IsVoid(ElementId materialId) =>
                voidMaterials != null && materialId != null && voidMaterials.Contains(materialId);

            foreach (var obj in ge)
            {
                switch (obj)
                {
                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face f in solid.Faces)
                        {
                            if (IsVoid(f.MaterialElementId)) continue;   // зона открывания и прочий «воздух»
                            EmitMesh(f.Triangulate(), emit);
                        }
                        break;
                    case Mesh m:
                        if (IsVoid(m.MaterialElementId)) break;
                        EmitMesh(m, emit);
                        break;
                    case GeometryInstance gi:
                        // GetInstanceGeometry() возвращает геометрию уже в координатах модели.
                        var inst = gi.GetInstanceGeometry();
                        if (inst != null) CollectTriangles(inst, voidMaterials, emit);
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

        /// <summary>
        /// Перевод «внутренние координаты документа → общие координаты площадки».
        /// GetTotalTransform() отдаёт обратное направление (из общих во
        /// внутренние), поэтому берём Inverse. Если положение площадки не задано,
        /// возвращается единичный — модель просто остаётся как есть.
        /// </summary>
        private static Transform SharedTransform(Document doc)
        {
            try
            {
                var location = doc.ActiveProjectLocation;
                var total = location?.GetTotalTransform();
                return total == null ? Transform.Identity : total.Inverse;
            }
            catch
            {
                return Transform.Identity;
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
            /// <summary>
            /// Раздел проекта («АР», «ОВ»), если модель собирается из нескольких
            /// документов. ElementId уникален только внутри своего документа: у
            /// 76-СУЗДАЛ ВК и ОВ делят 1034 одинаковых номера, и без раздела их
            /// паспорта затирали бы друг друга.
            /// </summary>
            public string Section { get; set; }
            public List<float> Positions { get; } = new List<float>();
            public List<float> Normals { get; } = new List<float>();
            public RoleMesh(string role, long? elementId = null) { Role = role; ElementId = elementId; }

            /// <summary>Ключ элемента: «id» или «РАЗДЕЛ:id».</summary>
            public string Key => ElementId.HasValue
                ? (string.IsNullOrEmpty(Section) ? ElementId.Value.ToString() : Section + ":" + ElementId.Value)
                : null;

            /// <summary>Имя узла/меша в glTF. Для target — «Element_&lt;id&gt;», чтобы веб-вьювер нашёл элемент по подстроке.</summary>
            public string GltfName => ElementId.HasValue ? "Element_" + Key : Role;
        }

        private static int RoleToMaterial(string role)
        {
            switch (role)
            {
                case "target": return 0;
                case "model": return 2;   // выгрузка всей модели — нейтральный непрозрачный
                default: return 1;        // neighbor
            }
        }

        private static object[] MaterialsJson() => new object[]
        {
            new { name = "target",   pbrMetallicRoughness = new { baseColorFactor = new[] { 0.94, 0.27, 0.27, 1.0 },  metallicFactor = 0.1, roughnessFactor = 0.7 }, doubleSided = true },
            new { name = "neighbor", pbrMetallicRoughness = new { baseColorFactor = new[] { 0.45, 0.48, 0.53, 0.45 }, metallicFactor = 0.1, roughnessFactor = 0.8 }, alphaMode = "BLEND", doubleSided = true },
            new { name = "model",    pbrMetallicRoughness = new { baseColorFactor = new[] { 0.62, 0.66, 0.63, 1.0 },  metallicFactor = 0.05, roughnessFactor = 0.85 }, doubleSided = true },
        };

        /// <param name="recenter">
        /// Точка, в которую рецентрирована геометрия (координаты Revit, футы). Её
        /// НЕОБХОДИМО сохранить в файле: каждый раздел проекта выгружается своим
        /// документом и центрируется по СВОИМ габаритам, поэтому без этой поправки
        /// АР, КЖ и ОВ не совмещаются — дом расходится на несколько домов. В extras
        /// кладём уже в осях glTF и метрах, чтобы читающей стороне осталось только
        /// прибавить.
        /// </param>
        /// <summary>
        /// Маленькая выгрузка целиком в памяти — замечание и его окружение.
        /// Дом сюда не поместится: см. <see cref="WriteGlb"/>.
        /// </summary>
        private static byte[] BuildGlb(List<RoleMesh> meshes, Dictionary<string, object> properties,
            XYZ recenter = null, Dictionary<string, object> extra = null)
        {
            using (var ms = new MemoryStream())
            {
                WriteGlb(ms, meshes, properties, recenter, extra);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Большая выгрузка — сразу во временный файл на диске. Возвращает путь,
        /// длину отдаёт через <paramref name="length"/>.
        /// </summary>
        private static string BuildGlbFile(List<RoleMesh> meshes, Dictionary<string, object> properties,
            XYZ recenter, out long length, Dictionary<string, object> extra = null)
        {
            string path = Path.Combine(Path.GetTempPath(), "tnovpro-" + Guid.NewGuid().ToString("N") + ".glb");
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    WriteGlb(fs, meshes, properties, recenter, extra);
                    length = fs.Length;
                }
            }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                throw;
            }
            return path;
        }

        /// <summary>
        /// Записать .glb В ПОТОК, не собирая его в памяти.
        ///
        /// 🔴 Прежде дом собирался в `MemoryStream`, и это был предел, а не
        /// экономия: внутри у него ОДИН `byte[]`, а `Capacity` — `Int32`, то есть
        /// 2 ГБ на весь двоичный кусок. Выгрузка 07.09 упала именно там
        /// (`set_Capacity(Int32)`) при двадцати свободных гигабайтах ОЗУ. Плюс
        /// готовый файл прежде существовал в памяти ЧЕТЫРЬМЯ копиями подряд:
        /// поток, `ToArray()`, паддинг, второй поток.
        ///
        /// Пишем в два прохода. Первый считает РАСКЛАДКУ — где в двоичном куске
        /// ляжет каждый меш; для этого байты не нужны, длина известна из числа
        /// чисел. Второй пишет заголовок, JSON и сами числа прямо в поток.
        /// Смещения — `long`: в `int` дом уже не помещается.
        /// </summary>
        private static void WriteGlb(Stream output, List<RoleMesh> meshes, Dictionary<string, object> properties,
            XYZ recenter, Dictionary<string, object> extra = null)
        {
            var bufferViews = new List<object>();
            var accessors = new List<object>();
            var gltfMeshes = new List<object>();
            var nodes = new List<object>();
            var nodeIdx = new List<int>();

            // Проход первый: раскладка. Всё выравнивается на 4 байта — у нас всё
            // и так float по 4 байта, но правило формата соблюдаем явно.
            long binLength = 0;
            long Align(long value) => (value + 3) & ~3L;

            foreach (var mesh in meshes)
            {
                int vcount = mesh.Positions.Count / 3;

                // POSITION
                binLength = Align(binLength);
                long posOffset = binLength;
                ComputeMinMax(mesh.Positions, out var pmin, out var pmax);
                long posLen = (long)mesh.Positions.Count * sizeof(float);
                binLength += posLen;
                int posView = bufferViews.Count;
                bufferViews.Add(new { buffer = 0, byteOffset = posOffset, byteLength = posLen, target = 34962 });
                int posAcc = accessors.Count;
                accessors.Add(new { bufferView = posView, componentType = 5126, count = vcount, type = "VEC3", min = pmin, max = pmax });

                // NORMAL
                binLength = Align(binLength);
                long nrmOffset = binLength;
                long nrmLen = (long)mesh.Normals.Count * sizeof(float);
                binLength += nrmLen;
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
                // числовым ElementId (GLTFLoader кладёт extras в mesh.userData → revitElementId)
                // и ролью (target/neighbor) — вьювер красит по ней, а не по материалу.
                var node = new Dictionary<string, object> { ["mesh"] = meshIdx, ["name"] = mesh.GltfName };
                if (mesh.ElementId.HasValue)
                {
                    var extras = new Dictionary<string, object>
                    {
                        ["revitElementId"] = mesh.ElementId.Value,
                        ["role"] = mesh.Role,
                    };
                    // Раздел рядом с номером: по нему читающая сторона и различает
                    // одинаковые ElementId из разных документов.
                    if (!string.IsNullOrEmpty(mesh.Section)) extras["tnovSection"] = mesh.Section;
                    node["extras"] = extras;
                }
                nodes.Add(node);
                nodeIdx.Add(nodes.Count - 1);
            }

            binLength = Align(binLength);

            // Свойства элементов едут ВМЕСТЕ с геометрией в корневых extras — вьюверу
            // (веб и нативный) не нужен отдельный запрос и серверная схема под них.
            var tnovpro = new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["units"] = "m",
                ["elements"] = properties ?? new Dictionary<string, object>(),
            };
            if (recenter != null)
            {
                // Куда уехало начало координат проекта: прибавив это к вершинам,
                // читающая сторона возвращает раздел в общую систему всех разделов.
                tnovpro["originGltf"] = ToGltf(recenter, XYZ.Zero);
            }
            // «Модель»: разделы полной выгрузки (sections) или метаданные приращения (delta).
            if (extra != null) foreach (var pair in extra) tnovpro[pair.Key] = pair.Value;
            var rootExtras = new Dictionary<string, object> { ["tnovpro"] = tnovpro };

            var gltf = new
            {
                asset = new { version = "2.0", generator = "TNovPro.Issues GLB exporter" },
                scene = 0,
                scenes = new[] { new { nodes = nodeIdx.ToArray() } },
                nodes,
                meshes = gltfMeshes,
                materials = MaterialsJson(),
                accessors,
                bufferViews,
                buffers = new[] { new { byteLength = binLength } },
                extras = rootExtras,
            };

            string json = JsonConvert.SerializeObject(gltf);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            // Паддинг чанков до кратности 4: JSON — пробелами (0x20), BIN — нулями.
            int jsonPad = (4 - jsonBytes.Length % 4) % 4;
            long total = 12L + 8 + jsonBytes.Length + jsonPad + 8 + binLength;

            // 🔴 Четыре гигабайта — предел САМОГО ФОРМАТА: длина файла и длина чанка
            // в заголовке .glb записаны 32-битными без знака. Упереться в него молча
            // нельзя: получился бы файл, который пишется, но не читается.
            if (total > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Модель не помещается в .glb: {total / 1024.0 / 1024 / 1024:N1} ГБ при пределе формата 4 ГБ. " +
                    "Снизьте детализацию или выгружайте разделы по отдельности.");
            }

            var ow = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            ow.Write(0x46546C67);                       // "glTF"
            ow.Write(2);                                // version
            ow.Write((uint)total);                      // длина файла целиком
            ow.Write((uint)(jsonBytes.Length + jsonPad));
            ow.Write(0x4E4F534A);                       // "JSON"
            ow.Write(jsonBytes);
            for (int i = 0; i < jsonPad; i++) ow.Write((byte)0x20);
            ow.Write((uint)binLength);
            ow.Write(0x004E4942);                       // "BIN\0"

            // Проход второй: сами числа. Смещения ОБЯЗАНЫ совпасть с раскладкой
            // первого прохода — поэтому выравнивание считается тем же `Align`.
            long written = 0;
            foreach (var mesh in meshes)
            {
                for (long pad = Align(written) - written; pad > 0; pad--) { ow.Write((byte)0); written++; }
                foreach (var f in mesh.Positions) ow.Write(f);
                written += (long)mesh.Positions.Count * sizeof(float);

                for (long pad = Align(written) - written; pad > 0; pad--) { ow.Write((byte)0); written++; }
                foreach (var f in mesh.Normals) ow.Write(f);
                written += (long)mesh.Normals.Count * sizeof(float);
            }
            for (long pad = binLength - written; pad > 0; pad--) ow.Write((byte)0);
            ow.Flush();
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
