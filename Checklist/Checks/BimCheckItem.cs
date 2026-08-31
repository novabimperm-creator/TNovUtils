using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TNovCommon;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Пункт BIM-проверки. ModelNameMarkers — сочетания в имени модели,
    /// при которых пункт виден. Пустой список = все модели.
    /// </summary>
    public sealed class BimCheckItem : ObservableObject
    {
        public const int StaleCalendarDays = 30;

        public string Id { get; set; }
        public string Title { get; set; }

        /// <summary>
        /// Сочетания в имени/заголовке модели. Пустой список — пункт для всех моделей.
        /// </summary>
        public List<string> ModelNameMarkers { get; set; } = new List<string>();

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }

        private DateTime _lastChangedAt;
        public DateTime LastChangedAt
        {
            get => _lastChangedAt;
            set
            {
                _lastChangedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOutdated));
                OnPropertyChanged(nameof(DisplayDate));
            }
        }

        private string _lastChangedBy;
        public string LastChangedBy
        {
            get => _lastChangedBy;
            set => SetProperty(ref _lastChangedBy, value);
        }

        private string _comment = "";
        public string Comment
        {
            get => _comment;
            set => SetProperty(ref _comment, value ?? "");
        }

        public string DisplayDate =>
            LastChangedAt.Year < 2000 ? "—" : LastChangedAt.ToString("dd.MM HH:mm");

        public bool IsOutdated
        {
            get
            {
                if (LastChangedAt.Year < 2000) return true;
                return (DateTime.Today - LastChangedAt.Date).TotalDays >= StaleCalendarDays;
            }
        }

        public bool IsVisibleFor(Document doc)
        {
            if (ModelNameMarkers == null || ModelNameMarkers.Count == 0)
                return true;
            return ModelNameRules.ContainsAny(doc, ModelNameMarkers.ToArray());
        }

        public static IReadOnlyList<BimCheckItem> Catalog()
        {
            return new[]
            {
                new BimCheckItem
                {
                    Id = "rf-link-conflicts",
                    Title = "Отсутствуют конфликты со связанной моделью РФ",
                    ModelNameMarkers = new List<string>()
                },
                new BimCheckItem
                {
                    Id = "axes-base-point",
                    Title = "Оси А и 1 пересекаются в базовой точке",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "floors-on-levels",
                    Title = "Уровни выставлены по верху жб плит перекрытий",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "walls-partitions-types",
                    Title = "Наружные стены и Перегородки смоделированы соответствующими разными типами",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "levels-names",
                    Title = "Уровни названы по структуре Код Отметка Название (пример - 05 +12.850 Этаж 5)",
                    ModelNameMarkers = new List<string>()
                },
                new BimCheckItem
                {
                    Id = "columns-as-columns",
                    Title = "Колонны и пилоны смоделированы инструментом Несущая колонна, в т.ч. в составе стен подземной части",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ." }
                },
                new BimCheckItem
                {
                    Id = "dwg-links-2d",
                    Title = "Связи DWG вставлены с опцией Только текущий вид",
                    ModelNameMarkers = new List<string>()
                },
                new BimCheckItem
                {
                    Id = "elems-not-needed",
                    Title = "В модели отсутствуют лишние элементы (не привязанные к объему здания)",
                    ModelNameMarkers = new List<string>()
                },
                new BimCheckItem
                {
                    Id = "balcony-doors-windows",
                    Title = "Выходы из квартир в летние помещения (в т.ч. одиночные двери) должны быть созданы в категории Окна",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "3d-furniture",
                    Title = "Мебель и сантехника смоделированы 3D-семействами",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "types-gm-table",
                    Title = "Имена типов и параметр Группа модели соответствуют Таблице параметров",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ." }
                },
                new BimCheckItem
                {
                    Id = "types-materials",
                    Title = "Имена типов элементов соответствуют их структуре и материалам",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ." }
                },
                new BimCheckItem
                {
                    Id = "correct-material-names",
                    Title = "Материалы элементов названы корректно",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ." }
                },
                new BimCheckItem
                {
                    Id = "brick-walls-alone",
                    Title = "Кладка стен не объединена с другими слоями (исключение - воздух)",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "windows-cut-walls",
                    Title = "Все проемы прорезаны (слои стен соединены)",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "base-of-walls",
                    Title = "Цоколь выполнен отдельной стеной из полнотелого кирпича или блока высотой 500 мм",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "brick-mark",
                    Title = "Марка кирпича и раствора в параметре A_Материал название у материалов кладки соответствуют проекту",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "full-underground",
                    Title = "Элементы подземной части смоделированы в полном объеме (в т.ч. узел цоколя с утеплением, мембраной и гидроизоляцией)",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "insulation-divided-layers",
                    Title = "Материалы утеплителя в модели разделены по слоям различной плотности в зависимости от конструкции фасада по проекту",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "entrance-group-ceiling",
                    Title = "Подшивка входных групп выполнена в категории Потолки, группа модели - Фасад",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "doors-gap-parameter",
                    Title = "Нижний зазор дверей выставлен параметром N_Зазор.Снизу (а не Высотой нижнего бруса, за редкими исключениями)",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "floors-divided-rooms",
                    Title = "Полы смоделированы по контуру отделки, заходят в проемы, разделены по помещениям",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "finishing-divided-rooms",
                    Title = "Отделочные стены смоделированы внутри каждого помещения отдельно (при наличии разделителей помещений стены делятся в месте расположения разделителя)",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "finishing-divided-stairs",
                    Title = "Отделка стен в лестничной клетке должна быть вырезана под лестницу и балки",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "concrete-beams-cut",
                    Title = "Смоделированы все бетонные перемычки по проекту, их объем вырезан из стен",
                    ModelNameMarkers = new List<string> { "-АР", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "roof-chrysotile",
                    Title = "Все элементы из хризотилцементного листа по проекту присутствуют в проекте, с учётом количества слоев",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-ПОФ", "_ПОФ" }
                },
                new BimCheckItem
                {
                    Id = "insulation-model",
                    Title = "Гидроизоляция присутствует в модели, в т.ч. нахлесты",
                    ModelNameMarkers = new List<string> { "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖФ", "-КЖ0", "-КЖ." }
                },
                new BimCheckItem
                {
                    Id = "types-under-upper",
                    Title = "Конструкции смоделированы типом с подзем или надзем в имени типа",
                    ModelNameMarkers = new List<string> { "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ." }
                },
                new BimCheckItem
                {
                    Id = "holes-ar",
                    Title = "Проемы в стенах соответствуют АР",
                    ModelNameMarkers = new List<string> { "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ." }
                },
                new BimCheckItem
                {
                    Id = "holes-st",
                    Title = "Проемы в стенах соответствуют КР",
                    ModelNameMarkers = new List<string> { "-АР-", "_АР" }
                },
                new BimCheckItem
                {
                    Id = "holes-tasks-copies",
                    Title = "Отверстия соответствуют актуальным заданиям, раскопированы по этажам и вырезаны (с полным прорезанием)",
                    ModelNameMarkers = new List<string> { "-АР", "_АР", "-КР-", "_КР", "-КЖ-", "_КЖ", "-КЖ0", "-КЖ." }
                }
            };
        }

        public static CheckStatus AggregateStatus(IReadOnlyList<BimCheckItem> items)
        {
            if (items == null || items.Count == 0)
                return CheckStatus.Outdated;

            int total = items.Count;
            int passed = 0;
            int outdated = 0;
            foreach (var item in items)
            {
                if (item.IsChecked) passed++;
                if (item.IsOutdated) outdated++;
            }

            if (passed == total && outdated == 0)
                return CheckStatus.Passed;

            // Красный: есть непройденные либо устарело строго больше половины.
            if (passed < total || outdated * 2 > total)
                return CheckStatus.Failed;

            return CheckStatus.Outdated;
        }
    }
}
