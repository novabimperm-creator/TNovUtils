using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TNovCommon;

namespace TNovUtils
{
    /// <summary>
    /// Данные Типофильтра для одного вида: собраны в контексте Revit API при открытии/обновлении окна.
    /// </summary>
    public class TypeFilterData
    {
        public Document Document;
        public ElementId ViewId;
        public string ViewName;
        public List<TypeFilterCategoryViewModel> Categories;
    }

    /// <summary>
    /// Единственное немодальное окно Типофильтра.
    /// </summary>
    public static class TypeFilterHost
    {
        private static TypeFilterWPF _window;

        public static void ShowOrActivate(UIApplication uiapp, TypeFilterData data)
        {
            if (_window != null)
            {
                _window.Load(data);
                if (_window.WindowState == System.Windows.WindowState.Minimized)
                    _window.WindowState = System.Windows.WindowState.Normal;
                _window.Activate();
                return;
            }

            _window = new TypeFilterWPF(data);
            _window.Closed += (s, e) => { _window = null; };

            new System.Windows.Interop.WindowInteropHelper(_window) { Owner = uiapp.MainWindowHandle };
            _window.Show();
        }
    }

    /// <summary>
    /// Мост «немодальное окно → Revit API». Действия из WPF ставятся в очередь ExternalEvent.
    /// </summary>
    public sealed class TypeFilterActionHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();
        public ExternalEvent Event { get; set; }

        public void Enqueue(Action<UIApplication> action)
        {
            _queue.Enqueue(action);
            Event?.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(app); }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка Revit-операции: " + ex, 4);
                    new InfoWindow280("Ошибка Revit-операции: " + ex.Message).ShowDialog();
                }
            }
        }

        public string GetName() => "TNov Типофильтр — Revit Action";
    }

    public static class TypeFilterRevitBridge
    {
        private static TypeFilterActionHandler _handler;

        /// <summary>Вызывать из IExternalCommand.Execute (ExternalEvent создается только в контексте API).</summary>
        public static void Initialize()
        {
            if (_handler != null) return;
            _handler = new TypeFilterActionHandler();
            _handler.Event = ExternalEvent.Create(_handler);
        }

        public static void Enqueue(Action<UIApplication> action)
        {
            if (_handler == null)
                throw new InvalidOperationException("TypeFilterRevitBridge не инициализирован");
            _handler.Enqueue(action);
        }
    }

    /// <summary>
    /// Операции Типофильтра. Все методы вызываются в контексте Revit API.
    /// </summary>
    public static class TypeFilterActions
    {
        private static readonly List<int> IgnoreCategoryId = new List<int>()
        {
            -2000500,
            -2008001,
            -2008066,
            -2000301
        };

        /// <summary>Категория, которую не добавляем в фильтры вида.</summary>
        private const long NoFilterCategoryId = -2001352;
        private const double DoubleEpsilon = 1e-6;

        #region Сбор данных

        public static TypeFilterData Collect(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;
            var comparer = new AlphanumComparatorFastString();

            var byCategory = new FilteredElementCollector(doc, activeView.Id).WhereElementIsNotElementType().ToElements()
                .Where(e => e.Category != null && !IgnoreCategoryId.Contains((int)IdValue(e.Category.Id)))
                .GroupBy(e => e.Category.Id);

            var categories = new List<TypeFilterCategoryViewModel>();
            foreach (var group in byCategory)
            {
                Category category = group.First().Category;
                var categoryViewModel = new TypeFilterCategoryViewModel()
                {
                    Category = category,
                    Name = category.Name,
                    IsSelected = false
                };

                var types = new Dictionary<ElementId, TypeFilterElementTypeViewModel>();
                bool sampledUntyped = false;
                foreach (Element element in group)
                {
                    categoryViewModel.ElementIds.Add(element.Id);
                    ElementType elementType = doc.GetElement(element.GetTypeId()) as ElementType;
                    if (elementType == null)
                    {
                        // Набор параметров у экземпляров одного типа одинаков — берем по одному образцу.
                        if (!sampledUntyped)
                        {
                            AddParameters(element, false, categoryViewModel.Parameters);
                            sampledUntyped = true;
                        }
                        continue;
                    }
                    if (!types.TryGetValue(elementType.Id, out var typeViewModel))
                    {
                        typeViewModel = new TypeFilterElementTypeViewModel()
                        {
                            ElementType = elementType,
                            Name = elementType.Name,
                            IsSelected = false
                        };
                        types.Add(elementType.Id, typeViewModel);
                        AddParameters(element, false, categoryViewModel.Parameters);
                        AddParameters(elementType, true, categoryViewModel.Parameters);
                    }
                    typeViewModel.ElementIds.Add(element.Id);
                }
                categoryViewModel.ElementTypes = types.Values.OrderBy(t => t.Name, comparer).ToList();
                categories.Add(categoryViewModel);
            }

            return new TypeFilterData
            {
                Document = doc,
                ViewId = activeView.Id,
                ViewName = activeView.Name,
                Categories = categories.OrderBy(c => c.Name, comparer).ToList()
            };
        }

        private static void AddParameters(Element element, bool isType, Dictionary<ElementId, TypeFilterParameterInfo> parameters)
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.Definition == null || parameter.StorageType == StorageType.None)
                    continue;
                ElementId id = parameter.Id;
                if (id == null || id == ElementId.InvalidElementId || parameters.ContainsKey(id))
                    continue;
                parameters.Add(id, new TypeFilterParameterInfo
                {
                    Id = id,
                    Name = parameter.Definition.Name,
                    DisplayName = parameter.Definition.Name,
                    IsTypeParameter = isType,
                    BuiltIn = (parameter.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID
                });
            }
        }

        /// <summary>Значения параметра у элементов выбранных категорий, с количеством.</summary>
        public static Dictionary<TypeFilterCategoryViewModel, List<TypeFilterParameterValueViewModel>> CollectValues(
            Document doc, IEnumerable<TypeFilterCategoryViewModel> categories, TypeFilterParameterInfo info)
        {
            var comparer = new AlphanumComparatorFastString();
            var result = new Dictionary<TypeFilterCategoryViewModel, List<TypeFilterParameterValueViewModel>>();
            foreach (TypeFilterCategoryViewModel category in categories)
            {
                var values = new Dictionary<string, TypeFilterParameterValueViewModel>();
                foreach (ElementId elementId in category.ElementIds)
                {
                    Element element = doc.GetElement(elementId);
                    if (element == null)
                        continue;
                    Parameter parameter = FindParameter(element, info)
                        ?? (doc.GetElement(element.GetTypeId()) is Element type ? FindParameter(type, info) : null);

                    string text;
                    bool isEmpty = false, isMissing = false;
                    if (parameter == null)
                    {
                        text = TypeFilterParameterValueViewModel.MissingValue;
                        isMissing = true;
                    }
                    else
                    {
                        text = parameter.HasValue
                            ? (parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString())
                            : null;
                        if (string.IsNullOrEmpty(text))
                        {
                            text = TypeFilterParameterValueViewModel.EmptyValue;
                            isEmpty = true;
                        }
                    }

                    if (!values.TryGetValue(text, out var value))
                    {
                        value = new TypeFilterParameterValueViewModel
                        {
                            Category = category,
                            Value = text,
                            IsEmpty = isEmpty,
                            IsMissing = isMissing,
                            StorageType = parameter?.StorageType ?? StorageType.None
                        };
                        values.Add(text, value);
                    }
                    value.ElementIds.Add(elementId);
                    if (parameter != null && !isEmpty)
                        AddRawValue(value, parameter);
                }

                // Пустые и отсутствующие — в конце списка.
                result[category] = values.Values
                    .OrderBy(v => v.IsMissing ? 2 : v.IsEmpty ? 1 : 0)
                    .ThenBy(v => v.Value, comparer)
                    .ToList();
            }
            return result;
        }

        private static Parameter FindParameter(Element element, TypeFilterParameterInfo info)
        {
            if (info.BuiltIn != BuiltInParameter.INVALID)
                return element.get_Parameter(info.BuiltIn);
            foreach (Parameter parameter in element.GetParameters(info.Name))
            {
                if (parameter.Id == info.Id)
                    return parameter;
            }
            return null;
        }

        private static void AddRawValue(TypeFilterParameterValueViewModel value, Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    value.Strings.Add(parameter.AsString());
                    break;
                case StorageType.Integer:
                    value.Integers.Add(parameter.AsInteger());
                    break;
                case StorageType.ElementId:
                    value.Ids.Add(parameter.AsElementId());
                    break;
                case StorageType.Double:
                    double d = parameter.AsDouble();
                    value.Min = Math.Min(value.Min, d);
                    value.Max = Math.Max(value.Max, d);
                    break;
            }
        }

        #endregion

        #region Действия с элементами

        public static void Hide(UIApplication app, TypeFilterData data, List<ElementId> ids)
        {
            Logger.Log("Скрытие элементов", 1);
            if (!TryGetView(app, data, out View view, out Document doc))
                return;
            ids = ExistingIds(doc, ids);
            if (ids.Count == 0)
                return;
            using (Transaction transaction = new Transaction(doc))
            {
                transaction.Start("Фильтр");
                view.HideElementsTemporary(ids);
                doc.Regenerate();
                transaction.Commit();
            }
        }

        public static void Isolate(UIApplication app, TypeFilterData data, List<ElementId> ids)
        {
            Logger.Log("Изоляция элементов", 1);
            if (!TryGetView(app, data, out View view, out Document doc))
                return;
            ids = ExistingIds(doc, ids);
            if (ids.Count == 0)
                return;
            using (Transaction transaction = new Transaction(doc))
            {
                transaction.Start("Фильтр");
                view.IsolateElementsTemporary(ids);
                doc.Regenerate();
                transaction.Commit();
            }
        }

        public static void Select(UIApplication app, TypeFilterData data, List<ElementId> ids)
        {
            Logger.Log("Выбор элементов", 1);
            if (!TryGetDocument(app, data, out Document doc))
                return;
            app.ActiveUIDocument.Selection.SetElementIds(ExistingIds(doc, ids));
        }

        private static List<ElementId> ExistingIds(Document doc, List<ElementId> ids) =>
            ids.Where(id => doc.GetElement(id) != null).ToList();

        private static bool TryGetDocument(UIApplication app, TypeFilterData data, out Document doc)
        {
            doc = null;
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null || data.Document == null || !data.Document.IsValidObject || !uidoc.Document.Equals(data.Document))
            {
                new InfoWindow280("Типофильтр открыт для другой модели. Нажмите «Обновить» в окне Типофильтра.").ShowDialog();
                return false;
            }
            doc = uidoc.Document;
            return true;
        }

        private static bool TryGetView(UIApplication app, TypeFilterData data, out View view, out Document doc)
        {
            view = null;
            if (!TryGetDocument(app, data, out doc))
                return false;
            if (doc.ActiveView == null || doc.ActiveView.Id != data.ViewId)
            {
                new InfoWindow280("Активный вид сменился. Нажмите «Обновить» в окне Типофильтра, чтобы работать с текущим видом.").ShowDialog();
                return false;
            }
            view = doc.ActiveView;
            return true;
        }

        #endregion

        #region Фильтры вида

        public static void CreateTypeFilter(UIApplication app, TypeFilterData data, List<TypeFilterElementTypeViewModel> selectedElementTypes, string filterName)
        {
            Logger.Log("Сценарий: создать фильтр", 1);
            if (!TryGetView(app, data, out View view, out Document doc))
                return;

            var elementTypes = selectedElementTypes
                .Where(t => t.ElementType.IsValidObject && t.ElementType.Category != null
                    && IdValue(t.ElementType.Category.Id) != NoFilterCategoryId)
                .ToList();
            List<ElementId> categoryIds = elementTypes.Select(t => t.ElementType.Category.Id).Distinct().ToList();
            IList<FilterRule> filterRules = new List<FilterRule>();
            ElementId parameter = new ElementId(BuiltInParameter.SYMBOL_NAME_PARAM);
            foreach (TypeFilterElementTypeViewModel elementTypeViewModel in elementTypes)
            {
#if R2022
                filterRules.Add(ParameterFilterRuleFactory.CreateEqualsRule(parameter, elementTypeViewModel.ElementType.Name, true));
#else
                filterRules.Add(ParameterFilterRuleFactory.CreateEqualsRule(parameter, elementTypeViewModel.ElementType.Name));
#endif
            }
            if (filterRules.Count == 0)
                return;

            var filters = filterRules.Select(r => (ElementFilter)new ElementParameterFilter(r)).ToList();
            CreateAndApplyFilter(doc, view, filterName, categoryIds, filters, "Не удалось создать фильтр по выбранным типам!");
        }

        public static void CreateParameterFilter(UIApplication app, TypeFilterData data, TypeFilterParameterInfo info,
            List<TypeFilterParameterValueViewModel> selectedValues, string filterName)
        {
            Logger.Log("Сценарий: создать фильтр по параметру «" + info.Name + "»", 1);
            if (!TryGetView(app, data, out View view, out Document doc))
                return;

            List<ElementId> categoryIds = selectedValues
                .Select(v => v.Category.Category.Id)
                .Where(id => IdValue(id) != NoFilterCategoryId)
                .Distinct()
                .ToList();
            if (categoryIds.Count == 0)
                return;

            if (!ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categoryIds).Contains(info.Id))
            {
                new InfoWindow280("Параметр «" + info.Name + "» нельзя использовать в фильтре вида для выбранных категорий.").ShowDialog();
                return;
            }

            // Отмечен сплошной диапазон чисел (между отмеченными нет неотмеченных значений) — одно правило «от…до».
            bool isRange = IsContiguousRange(selectedValues);
            var groups = isRange
                ? selectedValues.GroupBy(v => "").ToList()
                // Одинаковые значения из разных категорий — одно правило.
                : selectedValues.GroupBy(v => v.Value).ToList();
            var filters = new List<ElementFilter>();
            var skipped = new List<string>();
            var included = new List<TypeFilterParameterValueViewModel>();
            foreach (var group in groups)
            {
                ElementFilter filter = CreateValueFilter(info.Id, group.ToList());
                if (filter != null)
                {
                    filters.Add(filter);
                    included.AddRange(group);
                }
                else
                    skipped.Add(group.Key);
            }

            if (filters.Count == 0)
            {
                new InfoWindow280("Выбранные значения нельзя задать правилом фильтра: " + string.Join(", ", skipped)).ShowDialog();
                return;
            }

            if (string.IsNullOrWhiteSpace(filterName))
                filterName = BuildParameterFilterName(doc, categoryIds, info, included, isRange);

            if (CreateAndApplyFilter(doc, view, filterName, categoryIds, filters, "Не удалось создать фильтр по значениям параметра!")
                && skipped.Count > 0)
            {
                new InfoWindow280("Фильтр создан. Не вошли в фильтр значения: " + string.Join(", ", skipped)).ShowDialog();
            }
        }

        /// <summary>
        /// Отмечено не меньше двух числовых значений, и среди значений этих категорий между ними нет неотмеченных.
        /// </summary>
        private static bool IsContiguousRange(List<TypeFilterParameterValueViewModel> selectedValues)
        {
            if (selectedValues.Any(v => v.IsEmpty || v.IsMissing || v.StorageType != StorageType.Double))
                return false;
            var selectedKeys = new HashSet<string>(selectedValues.Select(v => v.Value));
            if (selectedKeys.Count < 2)
                return false;

            var all = selectedValues.Select(v => v.Category).Distinct()
                .SelectMany(c => c.ParameterValues ?? new List<TypeFilterParameterValueViewModel>())
                .Where(v => !v.IsEmpty && !v.IsMissing)
                .GroupBy(v => v.Value)
                .Select(g => new { g.Key, Min = g.Min(v => v.Min) })
                .OrderBy(x => x.Min)
                .ToList();
            int first = all.FindIndex(x => selectedKeys.Contains(x.Key));
            int last = all.FindLastIndex(x => selectedKeys.Contains(x.Key));
            return first >= 0 && all.Skip(first).Take(last - first + 1).All(x => selectedKeys.Contains(x.Key));
        }

        /// <summary>
        /// Имя фильтра «Категория_Параметр_Значение»: несколько категорий — «НескКатегорий»,
        /// диапазон — «A...B», несколько значений — «A,B,C»; числа округляются до целого.
        /// </summary>
        private static string BuildParameterFilterName(Document doc, List<ElementId> categoryIds, TypeFilterParameterInfo info,
            List<TypeFilterParameterValueViewModel> values, bool isRange)
        {
            string categoryPart = categoryIds.Count == 1
                ? (Category.GetCategory(doc, categoryIds[0])?.Name
                    ?? values.First(v => v.Category.Category.Id == categoryIds[0]).Category.Name)
                : "НескКатегорий";

            // Порядок значений: числа — по возрастанию, остальное — как в дереве.
            var comparer = new AlphanumComparatorFastString();
            List<TypeFilterParameterValueViewModel> ordered = values
                .GroupBy(v => v.Value)
                .Select(g => g.First())
                .OrderBy(v => v.StorageType == StorageType.Double && !v.IsEmpty ? v.Min : 0)
                .ThenBy(v => v.Value, comparer)
                .ToList();
            List<string> names = ordered.Select(v => RoundForName(v.Value, v.StorageType)).Distinct().ToList();

            string valuePart = isRange && names.Count > 1
                ? names.First() + "..." + names.Last()
                : string.Join(",", names);
            return categoryPart + "_" + info.Name + "_" + valuePart;
        }

        /// <summary>Число (у Double — возможно, с единицами: «1 500 мм») округляется до целого; прочий текст — как есть.</summary>
        private static string RoundForName(string value, StorageType storageType)
        {
            string pattern = storageType == StorageType.Double
                ? @"^\s*([-+]?\d[\d   ]*(?:[.,]\d+)?)\s*\D*$"
                : @"^\s*([-+]?\d+(?:[.,]\d+)?)\s*$";
            Match match = Regex.Match(value, pattern);
            if (!match.Success)
                return value;
            string number = Regex.Replace(match.Groups[1].Value, @"[\s  ]", "").Replace(',', '.');
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return value;
            return Math.Round(d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Правило «параметр = значение» для группы значений с одинаковым отображением; null — не выразить.</summary>
        private static ElementFilter CreateValueFilter(ElementId parameterId, List<TypeFilterParameterValueViewModel> values)
        {
            if (values.Any(v => v.IsMissing))
                return null;

            if (values.Any(v => v.IsEmpty))
            {
#if R2022
                return null;
#else
                return new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasNoValueParameterRule(parameterId));
#endif
            }

            var rules = new List<ElementFilter>();
            switch (values[0].StorageType)
            {
                case StorageType.String:
                    foreach (string s in values.SelectMany(v => v.Strings).Distinct())
#if R2022
                        rules.Add(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(parameterId, s, true)));
#else
                        rules.Add(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(parameterId, s)));
#endif
                    break;
                case StorageType.Integer:
                    foreach (int i in values.SelectMany(v => v.Integers).Distinct())
                        rules.Add(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(parameterId, i)));
                    break;
                case StorageType.ElementId:
                    foreach (ElementId id in values.SelectMany(v => v.Ids).Distinct())
                        rules.Add(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(parameterId, id)));
                    break;
                case StorageType.Double:
                    // Значения, которые отображаются одинаково (округление), — диапазоном [min; max].
                    double min = values.Min(v => v.Min);
                    double max = values.Max(v => v.Max);
                    rules.Add(new ElementParameterFilter(new List<FilterRule>
                    {
                        ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, min, DoubleEpsilon),
                        ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, max, DoubleEpsilon)
                    }));
                    break;
            }

            if (rules.Count == 0)
                return null;
            return rules.Count == 1 ? rules[0] : new LogicalOrFilter(rules);
        }

        private static bool CreateAndApplyFilter(Document doc, View view, string filterName, List<ElementId> categoryIds,
            List<ElementFilter> filters, string errorMessage)
        {
            try
            {
                using (Transaction transaction = new Transaction(doc))
                {
                    transaction.Start("Фильтр");
                    ElementFilter elementFilter = filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters);
                    ParameterFilterElement newFilter = ParameterFilterElement.Create(doc, GetUniqueFilterName(doc, filterName), categoryIds, elementFilter);
                    //проверяем, включен ли шаблон вида и контролирует ли шаблон фильтры
                    bool addFilterToTemplate = false;
                    ElementId templateId = view.get_Parameter(BuiltInParameter.VIEW_TEMPLATE)?.AsElementId();
                    if (templateId != null && IdValue(templateId) != -1)
                    {
                        //проверяем шаблон вида на предмет отключенной галочки "Фильтры"
                        ElementId elementId = new ElementId(-1006964); //id отключенной галочки Фильтры (получен опытным путем)
                        View template = (View)doc.GetElement(templateId);
                        bool filtersDisabled = template.GetNonControlledTemplateParameterIds().Contains(elementId);
                        if (!filtersDisabled)
                        {
                            addFilterToTemplate = true;
                            //добавляем фильтр к шаблону вида
                            Logger.Log("Добавляем фильтр к шаблону", 1);
                            template.AddFilter(newFilter.Id);
                            template.SetFilterVisibility(newFilter.Id, false);
                        }
                    }
                    if (!addFilterToTemplate)
                    {
                        //добавляем фильтр к виду
                        Logger.Log("Добавляем фильтр к виду", 1);
                        view.AddFilter(newFilter.Id);
                        view.SetFilterVisibility(newFilter.Id, false);
                    }
                    doc.Regenerate();
                    transaction.Commit();
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("Не удалось создать фильтр: " + ex.Message, 4);
                new InfoWindow280(errorMessage).ShowDialog();
                return false;
            }
        }

        private static string GetUniqueFilterName(Document doc, string filterName)
        {
            // Символы, недопустимые в именах элементов Revit.
            foreach (char c in "\\:{}[]|;<>?`~")
                filterName = filterName.Replace(c.ToString(), "");
            filterName = filterName.Trim();
            if (filterName.Length == 0)
                filterName = "Типофильтр";

            var existing = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Select(f => f.Name));
            int num = 0;
            string uniqueName = filterName;
            while (existing.Contains(uniqueName))
                uniqueName = string.Format("{0} ({1})", filterName, ++num);
            return uniqueName;
        }

        #endregion

        public static long IdValue(ElementId id)
        {
#if R2022
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }
    }
}
