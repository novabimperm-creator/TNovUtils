// Перенесено из monumenthunny-dev/tnovpro-issues-revit (Revit/ModelLinks.cs) для «Модели» TNovPRO:
// вопрос о модели на сайте + синхронизация с центральной моделью (2026-09-28).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using TNovCommon;

namespace TNovUtils.Issues.ModelSync
{
    /// <summary>
    /// Связи основного документа: что подключено к модели и в каком состоянии.
    ///
    /// Зачем отдельным местом. Раньше выгрузка сама пробегала по
    /// <see cref="RevitLinkInstance"/>, молча пропускала всё, у чего нет
    /// загруженного документа, и писала в отчёт одно число: «не загружено: 3».
    /// По этому числу нельзя было понять, ЧЕГО не хватает — у 76-СУЗДАЛ так
    /// три выгрузки подряд уезжали без раздела СС, и никто этого не видел.
    /// Теперь связи собираются в описания с именем, разделом и состоянием:
    /// их показывают человеку перед выгрузкой (см. UI.LinkPickerWindow) и
    /// называют поимённо в отчёте.
    /// </summary>
    public static class ModelLinks
    {
        /// <summary>Одна связь основного документа (все её экземпляры — одной строкой).</summary>
        public sealed class Link
        {
            /// <summary>Экземпляр, из которого берётся положение раздела в мире хоста.</summary>
            public ElementId InstanceId { get; set; }
            public ElementId TypeId { get; set; }

            /// <summary>Имя файла связи: «76-СУЗДАЛ.23_СС_С1.rvt».</summary>
            public string FileName { get; set; }

            /// <summary>Раздел так, как он ляжет в .glb: «СС_С1».</summary>
            public string Section { get; set; }

            /// <summary>Есть ли у связи открытый документ — только такую можно выгрузить.</summary>
            public bool Loaded { get; set; }

            /// <summary>Состояние человеческими словами: «загружена», «выгружена из сеанса», …</summary>
            public string Status { get; set; }

            /// <summary>Сколько экземпляров этой связи стоит в модели.</summary>
            public int Instances { get; set; }

            /// <summary>Путь к файлу связи — по нему видно, та ли это папка.</summary>
            public string Path { get; set; }

            /// <summary>Связь внутри другой связи: её тянет за собой родитель.</summary>
            public bool Nested { get; set; }

            /// <summary>Отметка человека в окне выбора.</summary>
            public bool Selected { get; set; }
        }

        /// <summary>
        /// Собрать связи основного документа. Экземпляры одной и той же связи
        /// сливаются в одну строку: раздел выгружается один раз (так было и
        /// раньше — дедупликация шла по имени документа).
        /// </summary>
        public static List<Link> Collect(Document host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            var byType = new Dictionary<long, Link>();
            var order = new List<long>();

            foreach (RevitLinkInstance li in new FilteredElementCollector(host)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var typeId = li.GetTypeId();
                var key = typeId == null ? -1L : typeId.LongValue();

                if (byType.TryGetValue(key, out var known))
                {
                    ++known.Instances;
                    continue;
                }

                var type = host.GetElement(typeId) as RevitLinkType;
                Document linked = null;
                try { linked = li.GetLinkDocument(); } catch { }

                var name = FileNameOf(type, li, linked);
                var link = new Link
                {
                    InstanceId = li.Id,
                    TypeId = typeId,
                    FileName = name,
                    Section = SectionOf(linked != null ? linked.Title : Path.GetFileNameWithoutExtension(name)),
                    Loaded = linked != null,
                    Status = StatusOf(type, linked),
                    Instances = 1,
                    Path = PathOf(type),
                    Nested = IsNested(type),
                };

                byType[key] = link;
                order.Add(key);
            }

            return order.Select(k => byType[k]).ToList();
        }

        /// <summary>
        /// Загрузить связь в сеанс. Нужна, когда человек отметил в окне выбора
        /// раздел, который сейчас выгружен: без документа выгружать нечего.
        ///
        /// 🔴 Загрузка МЕНЯЕТ модель (Revit запомнит, что связь загружена, и
        /// предложит сохранить файл при закрытии) и не может идти внутри
        /// транзакции — поэтому команда выгрузки объявлена Manual, а не ReadOnly.
        /// </summary>
        /// <returns>null — получилось; иначе текст, почему не вышло.</returns>
        public static string Load(Document host, Link link)
        {
            if (host == null || link == null) return "нечего загружать";
            var type = host.GetElement(link.TypeId) as RevitLinkType;
            if (type == null) return "связь не найдена в модели";

            try
            {
                var result = type.Load();
                if (result == null) return "Revit не сказал, чем кончилось";
                return LoadResultText(result.LoadResult);   // null = получилось
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Раздел из имени документа: «76-СУЗДАЛ.23_СС_С1» → «СС_С1».
        /// Правило то же, что у выгрузки: шифр объекта (до первого «_») отбрасывается.
        /// </summary>
        public static string SectionOf(string title)
        {
            if (string.IsNullOrEmpty(title)) return "модель";
            var parts = title.Split('_');
            if (parts.Length <= 1) return title;
            return string.Join("_", parts, 1, parts.Length - 1);
        }

        private static string FileNameOf(RevitLinkType type, RevitLinkInstance instance, Document linked)
        {
            try { if (type != null && !string.IsNullOrWhiteSpace(type.Name)) return type.Name; } catch { }
            if (linked != null && !string.IsNullOrWhiteSpace(linked.Title)) return linked.Title + ".rvt";
            try { if (instance != null && !string.IsNullOrWhiteSpace(instance.Name)) return instance.Name; } catch { }
            return "связь без имени";
        }

        private static string StatusOf(RevitLinkType type, Document linked)
        {
            if (type != null)
            {
                try
                {
                    switch (type.GetLinkedFileStatus())
                    {
                        case LinkedFileStatus.Loaded: return "загружена";
                        case LinkedFileStatus.Unloaded: return "выгружена из сеанса";
                        case LinkedFileStatus.NotFound: return "файл не найден по пути";
                        case LinkedFileStatus.LocallyUnloaded: return "выгружена локально";
                        case LinkedFileStatus.InClosedWorkset: return "в закрытом рабочем наборе";
                        case LinkedFileStatus.CanBeUpgraded: return "от старой версии Revit, нужно обновление";
                        case LinkedFileStatus.Imported: return "импортирована, а не связана";
                        case LinkedFileStatus.Invalid: return "ссылка испорчена";
                    }
                }
                catch { /* состояние API нам не отдал — судим по документу */ }
            }
            return linked != null ? "загружена" : "не загружена";
        }

        private static string PathOf(RevitLinkType type)
        {
            if (type == null) return "";
            try
            {
                var reference = type.GetExternalFileReference();
                if (reference == null) return "";
                var model = reference.GetAbsolutePath();
                if (model == null || model.Empty) model = reference.GetPath();
                return model == null ? "" : ModelPathUtils.ConvertModelPathToUserVisiblePath(model);
            }
            catch { return ""; }
        }

        private static bool IsNested(RevitLinkType type)
        {
            try { return type != null && type.IsNestedLink; } catch { return false; }
        }

        private static string LoadResultText(LinkLoadResultType result)
        {
            switch (result)
            {
                case LinkLoadResultType.LinkLoaded: return null;
                case LinkLoadResultType.UsedExisting: return null;   // документ уже открыт — это годится
                case LinkLoadResultType.LinkExists: return null;
                case LinkLoadResultType.LinkNotFound: return "файл не найден по сохранённому пути";
                case LinkLoadResultType.LinkNotOpenable: return "файл не открывается (занят или испорчен)";
                case LinkLoadResultType.LinkMayBeUpgraded: return "файл от старой версии Revit — нужно обновление";
                case LinkLoadResultType.SameModelAsHost: return "это тот же файл, что и основной документ";
                case LinkLoadResultType.SameCentralModelAsHost: return "тот же центральный файл, что и у основного документа";
                case LinkLoadResultType.LinkOpenAsHost: return "файл уже открыт как основной документ";
                case LinkLoadResultType.ExternalServerMissing: return "нет сервера, который хранит связь";
                case LinkLoadResultType.LinkNotLoadedOtherError: return "Revit не смог загрузить (причина не названа)";
                default: return "Revit отказал: " + result;
            }
        }
    }
}
