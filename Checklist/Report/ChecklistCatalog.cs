#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TNovUtils.Checklist.Report
{
    /// <summary>Автопроверка: номер в {docName},autocheck.json, название и маркеры имени модели.</summary>
    public sealed class AutoCheckDef
    {
        public AutoCheckDef(int number, string title, string[] markers)
        {
            Number = number;
            Title = title;
            Markers = markers;
        }

        public int Number { get; }
        public string Title { get; }

        /// <summary>null — проверка для всех моделей.</summary>
        public string[] Markers { get; }

        public bool AppliesTo(string modelName) =>
            Markers == null || ChecklistCatalog.ContainsAny(modelName, Markers);
    }

    /// <summary>BIM-проверка: id в {docName},BIM проверки.json, название и маркеры имени модели.</summary>
    public sealed class BimCheckDef
    {
        public BimCheckDef(string id, string title, params string[] markers)
        {
            Id = id;
            Title = title;
            Markers = markers ?? Array.Empty<string>();
        }

        public string Id { get; }
        public string Title { get; }

        /// <summary>Пустой массив — пункт для всех моделей.</summary>
        public string[] Markers { get; }

        public bool AppliesTo(string modelName) =>
            Markers.Length == 0 || ChecklistCatalog.ContainsAny(modelName, Markers);
    }

    /// <summary>
    /// Каталог пунктов Чек-листа без зависимости от Revit API.
    /// Файл общий: компилируется в TNovUtils и подключается ссылкой в TNovDesktop,
    /// поэтому здесь нельзя использовать ни Revit API, ни TNovCommon.
    /// </summary>
    public static class ChecklistCatalog
    {
        // ---- Маркеры имени модели (используются и ModelNameRules для Document) ----
        public static readonly string[] ArOrPofMarkers = { "-АР-", "_АР", "-ПОФ-", "_ПОФ" };
        public static readonly string[] ArMarkers = { "-АР-", "_АР" };
        public static readonly string[] RebarNoMarkMarkers = { "-КЖ-", "_КЖ", "-КР-", "_КР", "-АР-", "_АР" };
        public static readonly string[] NoPartsMarkers = { "-АР-", "_АР", "-ПОФ-", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ" };
        // Маркеры ВК/ОВ — как у сценария ВК ОВ в MEPSpec (без ПТ и ТС)
        public static readonly string[] VkOvMarkers = { "-ВК", "_ВК", "-ОВ", "_ОВ" };
        // Связи с кабельными лотками — для проверки арматуры над лотками
        public static readonly string[] ElSsPsMarkers = { "-ЭЛ", "_ЭЛ", "-СС", "_СС", "-ПС", "_ПС" };

        // ---- Автопроверки: номера и названия (номера — ключи в autocheck.json) ----
        public const int GridsLevelsLinksNumber = 1;
        public const int AntiMirrorNumber = 2;
        public const int RebarNoMarkNumber = 3;
        public const int NoPartsNumber = 4;
        public const int LintelsNoMarkNumber = 5;
        public const int EvacuationRoutesNumber = 6;
        public const int UnplacedRoomsNumber = 7;
        public const int RoomDepartmentNumber = 8;
        public const int AdskPostcheckNumber = 9;
        public const int RfCoordinationNumber = 10;
        public const int PipeAccessoriesOverTraysNumber = 11;
        public const int WetRoomsOverElectricalNumber = 12;
        public const int DwgCurrentViewOnlyNumber = 13;

        public const string GridsLevelsLinksTitle = "Оси, уровни, связи";
        public const string AntiMirrorTitle = "Антизеркало";
        public const string RebarNoMarkTitle = "Арматура без марки";
        public const string NoPartsTitle = "Отсутствуют элементы категории Части";
        public const string LintelsNoMarkTitle = "Перемычки без марки";
        public const string EvacuationRoutesTitle = "Смоделированы пути эвакуации";
        public const string UnplacedRoomsTitle = "Неразмещенные помещения";
        public const string RoomDepartmentTitle = "Заполненность Назначения помещений";
        public const string AdskPostcheckTitle = "Постпроверка ADSK";
        public const string RfCoordinationTitle = "Отсутствуют проблемы координации с файлом РФ";
        public const string PipeAccessoriesOverTraysTitle = "Арматура и заглушки труб не над кабельными лотками";
        public const string WetRoomsOverElectricalTitle = "Отсутствуют влажные помещения над электрощитовыми";
        public const string DwgCurrentViewOnlyTitle = "Связи DWG вставлены с опцией Только текущий вид";

        /// <summary>Те же правила, что в конструкторе CheckRegistry.</summary>
        public static readonly IReadOnlyList<AutoCheckDef> AutoChecks = new[]
        {
            new AutoCheckDef(GridsLevelsLinksNumber, GridsLevelsLinksTitle, null),
            new AutoCheckDef(RfCoordinationNumber, RfCoordinationTitle, null),
            new AutoCheckDef(DwgCurrentViewOnlyNumber, DwgCurrentViewOnlyTitle, null),
            new AutoCheckDef(AntiMirrorNumber, AntiMirrorTitle, ArOrPofMarkers),
            new AutoCheckDef(RebarNoMarkNumber, RebarNoMarkTitle, RebarNoMarkMarkers),
            new AutoCheckDef(NoPartsNumber, NoPartsTitle, NoPartsMarkers),
            new AutoCheckDef(LintelsNoMarkNumber, LintelsNoMarkTitle, ArMarkers),
            new AutoCheckDef(EvacuationRoutesNumber, EvacuationRoutesTitle, ArMarkers),
            new AutoCheckDef(UnplacedRoomsNumber, UnplacedRoomsTitle, ArMarkers),
            new AutoCheckDef(RoomDepartmentNumber, RoomDepartmentTitle, ArMarkers),
            new AutoCheckDef(WetRoomsOverElectricalNumber, WetRoomsOverElectricalTitle, ArMarkers),
            new AutoCheckDef(AdskPostcheckNumber, AdskPostcheckTitle, VkOvMarkers),
            new AutoCheckDef(PipeAccessoriesOverTraysNumber, PipeAccessoriesOverTraysTitle, VkOvMarkers)
        };

        // ---- Допуски устаревания — как в окне Чек-листа ----
        public const int AutoStaleCalendarDays = 7;
        public const int BimStaleCalendarDays = 30;

        /// <summary>BIM-проверки (ручной чек-лист). Порядок — как в окне Чек-листа.</summary>
        public static readonly IReadOnlyList<BimCheckDef> BimChecks = new[]
        {
            new BimCheckDef("axes-base-point",
                "Оси А и 1 пересекаются в базовой точке",
                "-АР", "_АР"),
            new BimCheckDef("floors-on-levels",
                "Уровни выставлены по верху жб плит перекрытий",
                "-АР", "_АР"),
            new BimCheckDef("walls-partitions-types",
                "Наружные стены и Перегородки смоделированы соответствующими разными типами",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("levels-names",
                "Уровни названы по структуре Код Отметка Название (пример - 05 +12.850 Этаж 5)"),
            new BimCheckDef("columns-as-columns",
                "Колонны и пилоны смоделированы инструментом Несущая колонна, в т.ч. в составе стен подземной части",
                "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ."),
            new BimCheckDef("elems-not-needed",
                "В модели отсутствуют лишние элементы (не привязанные к объему здания)"),
            new BimCheckDef("balcony-doors-windows",
                "Выходы из квартир в летние помещения (в т.ч. одиночные двери) должны быть созданы в категории Окна",
                "-АР", "_АР"),
            new BimCheckDef("3d-furniture",
                "Мебель и сантехника смоделированы 3D-семействами",
                "-АР", "_АР"),
            new BimCheckDef("types-gm-table",
                "Имена типов и параметр Группа модели соответствуют Таблице параметров",
                "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ."),
            new BimCheckDef("types-materials",
                "Имена типов элементов соответствуют их структуре и материалам",
                "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ."),
            new BimCheckDef("correct-material-names",
                "Материалы элементов названы корректно",
                "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ."),
            new BimCheckDef("brick-walls-alone",
                "Кладка стен не объединена с другими слоями (исключение - воздух)",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("windows-cut-walls",
                "Все проемы прорезаны (слои стен соединены)",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("base-of-walls",
                "Цоколь выполнен отдельной стеной из полнотелого кирпича или блока высотой 500 мм",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("brick-mark",
                "Марка кирпича и раствора в параметре A_Материал название у материалов кладки соответствуют проекту",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("full-underground",
                "Элементы подземной части смоделированы в полном объеме (в т.ч. узел цоколя с утеплением, мембраной и гидроизоляцией)",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("insulation-divided-layers",
                "Материалы утеплителя в модели разделены по слоям различной плотности в зависимости от конструкции фасада по проекту",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("entrance-group-ceiling",
                "Подшивка входных групп выполнена в категории Потолки, группа модели - Фасад",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("doors-gap-parameter",
                "Нижний зазор дверей выставлен параметром N_Зазор.Снизу (а не Высотой нижнего бруса, за редкими исключениями)",
                "-АР", "_АР"),
            new BimCheckDef("floors-divided-rooms",
                "Полы смоделированы по контуру отделки, заходят в проемы, разделены по помещениям",
                "-АР", "_АР"),
            new BimCheckDef("finishing-divided-rooms",
                "Отделочные стены смоделированы внутри каждого помещения отдельно (при наличии разделителей помещений стены делятся в месте расположения разделителя)",
                "-АР", "_АР"),
            new BimCheckDef("finishing-divided-stairs",
                "Отделка стен в лестничной клетке должна быть вырезана под лестницу и балки",
                "-АР", "_АР"),
            new BimCheckDef("concrete-beams-cut",
                "Смоделированы все бетонные перемычки по проекту, их объем вырезан из стен",
                "-АР", "_АР"),
            new BimCheckDef("roof-chrysotile",
                "Все элементы из хризотилцементного листа по проекту присутствуют в проекте, с учётом количества слоев",
                "-АР", "_АР", "-ПОФ", "_ПОФ"),
            new BimCheckDef("insulation-model",
                "Гидроизоляция присутствует в модели, в т.ч. нахлесты",
                "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ."),
            new BimCheckDef("types-under-upper",
                "Конструкции смоделированы типом с подзем или надзем в имени типа",
                "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ."),
            new BimCheckDef("holes-ar",
                "Проемы в стенах соответствуют АР",
                "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ."),
            new BimCheckDef("holes-st",
                "Проемы в стенах соответствуют КР",
                "-АР-", "_АР"),
            new BimCheckDef("holes-tasks-copies",
                "Отверстия соответствуют актуальным заданиям, раскопированы по этажам и вырезаны (с полным прорезанием)",
                "-АР", "_АР", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ.")
        };

        public static IEnumerable<AutoCheckDef> AutoChecksFor(string modelName) =>
            AutoChecks.Where(c => c.AppliesTo(modelName));

        public static IEnumerable<BimCheckDef> BimChecksFor(string modelName) =>
            BimChecks.Where(c => c.AppliesTo(modelName));

        public static bool ContainsAny(string name, params string[] markers)
        {
            if (string.IsNullOrEmpty(name) || markers == null || markers.Length == 0) return false;
            foreach (var marker in markers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                if (name.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }
}
