using Autodesk.Revit.DB;

namespace LevelMover.Core
{
    /// <summary>
    /// Где у элемента лежит уровень и где — смещение от него.
    ///
    /// Единого параметра уровня в Revit нет: у стены это «Зависимость снизу», у колонны
    /// «Базовый уровень», у перекрытия «Уровень», у трубы «Базовый уровень». Здесь собраны все
    /// известные варианты, и у элемента берётся первый подошедший.
    ///
    /// Порядок внутри списков значим: сначала параметры, которые двигают геометрию, и только
    /// потом «уровень для спецификации» — иначе у стены нашёлся бы спецификационный параметр,
    /// а сама стена осталась бы на месте.
    /// </summary>
    internal static class LevelBinding
    {
        private static readonly BuiltInParameter[] BaseLevelParams =
        {
            BuiltInParameter.WALL_BASE_CONSTRAINT,              // стены, фундаментные стены, шахты
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,           // колонны, несущие колонны
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,           // лестницы
            BuiltInParameter.ROOF_BASE_LEVEL_PARAM,             // крыши
            BuiltInParameter.ROOM_LEVEL_ID,                     // помещения
            BuiltInParameter.RBS_START_LEVEL_PARAM,             // трубы, воздуховоды, короба
            BuiltInParameter.LEVEL_PARAM,                       // перекрытия, фундаментные плиты
            BuiltInParameter.FAMILY_LEVEL_PARAM,                // экземпляры семейств на уровне
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,    // балки, связи, каркас
            BuiltInParameter.IMPORT_BASE_LEVEL,                 // импорт DWG
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,              // уровень только для спецификации
            BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM
        };

        private static readonly BuiltInParameter[] BaseOffsetParams =
        {
            BuiltInParameter.WALL_BASE_OFFSET,
            BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
            BuiltInParameter.STAIRS_BASE_OFFSET,
            BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM,
            BuiltInParameter.ROOM_LOWER_OFFSET,
            BuiltInParameter.RBS_OFFSET_PARAM,
            BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,
            BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
            BuiltInParameter.INSTANCE_ELEVATION_PARAM,
            BuiltInParameter.IMPORT_BASE_LEVEL_OFFSET
        };

        private static readonly BuiltInParameter[] TopLevelParams =
        {
            BuiltInParameter.WALL_HEIGHT_TYPE,                  // «Зависимость сверху» стены
            BuiltInParameter.FAMILY_TOP_LEVEL_PARAM,            // верх колонны
            BuiltInParameter.STAIRS_TOP_LEVEL_PARAM,
            BuiltInParameter.RBS_END_LEVEL_PARAM
        };

        private static readonly BuiltInParameter[] TopOffsetParams =
        {
            BuiltInParameter.WALL_TOP_OFFSET,
            BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
            BuiltInParameter.STAIRS_TOP_OFFSET
        };

        public static Parameter FindBaseLevel(Element element) => FindLevel(element, BaseLevelParams);

        public static Parameter FindTopLevel(Element element) => FindLevel(element, TopLevelParams);

        public static Parameter FindBaseOffset(Element element) => FindOffset(element, BaseOffsetParams);

        public static Parameter FindTopOffset(Element element) => FindOffset(element, TopOffsetParams);

        /// <summary>
        /// Параметр возвращается и когда он только для чтения: вызывающий по этому различает
        /// «уровня у элемента нет вовсе» и «уровень есть, но заблокирован» — причины в отчёте разные.
        /// </summary>
        private static Parameter FindLevel(Element element, BuiltInParameter[] candidates)
        {
            foreach (BuiltInParameter id in candidates)
            {
                Parameter parameter = element.get_Parameter(id);
                if (parameter != null && parameter.StorageType == StorageType.ElementId) return parameter;
            }

            return null;
        }

        private static Parameter FindOffset(Element element, BuiltInParameter[] candidates)
        {
            foreach (BuiltInParameter id in candidates)
            {
                Parameter parameter = element.get_Parameter(id);
                if (parameter != null && parameter.StorageType == StorageType.Double && !parameter.IsReadOnly)
                {
                    return parameter;
                }
            }

            return null;
        }
    }
}
