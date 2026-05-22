using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using TNovCommon;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    public class TypeFilter : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string TNovClassName = "Типофильтр"; DateTime dateTime = DateTime.Now; string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;

            //проверка подключения, запись в журнал
            if(ServerUtils.CheckConnection(TNovClassName, TNovVersion)==false) return Result.Failed;

            // создание log - файла
            Logger.Initialize(TNovClassName,dateTime,TNovVersion);


            var viewModel0 = new AppVersionViewModel();

            string jsonpath0 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TNovClient/TNovSettings.json");
            viewModel0 = JsonConvert.DeserializeObject<AppVersionViewModel>(File.ReadAllText(jsonpath0));

            if (viewModel0.extendedLogs)

            {
                var qViewModel = new QuestionWindowViewModel();
                qViewModel.headtxt = "Включены расширенные логи. " +
                    "Плагин будет работать медленнее, но соберет больше данных. " +
                    "Выключить расширенные логи для ускорения работы?";
                var qwpfview = new QuestionWindow280(qViewModel);
                qViewModel.CloseRequest += (s, e) => qwpfview.Close();
                bool? qok = qwpfview.ShowDialog();
                if (qok != null && qok == true) { Logger.TurnOffExtendedLogs(); } else Logger.Log("Расширенные логи вкл", 2);
            }

            Autodesk.Revit.UI.Selection.Selection selection = commandData.Application.ActiveUIDocument.Selection;
            View activeView = doc.ActiveView;
            List<int> ignoreCategoryId = new List<int>()
      {
        -2000500,
        -2008001,
        -2008066,
        -2000301
      };
            List<Category> list1 = new FilteredElementCollector(doc, activeView.Id).WhereElementIsNotElementType().ToElements()
                .Where<Element>((Func<Element, bool>)(e => e.Category != null))
                .Where<Element>((Func<Element, bool>)(e => !ignoreCategoryId.Contains(e.Category.Id.IntegerValue)))
                .Select<Element, Category>((Func<Element, Category>)(e => e.Category))
                .Distinct<Category>((IEqualityComparer<Category>)new CategoryComparer())
                .OrderBy<Category, string>((Func<Category, string>)(c => c.Name), 
                (IComparer<string>)new AlphanumComparatorFastString())
                .ToList<Category>();
            List<TypeFilterCategoryViewModel> categories = new List<TypeFilterCategoryViewModel>();
            foreach (Category category1 in list1)
            {
                Category category = category1;
                TypeFilterCategoryViewModel categoryViewModel = new TypeFilterCategoryViewModel()
                {
                    Category = category,
                    Name = category.Name,
                    IsSelected = false,
                    ElementTypes = new List<TypeFilterElementTypeViewModel>()
                };
                foreach (ElementType elementType in new FilteredElementCollector(doc, activeView.Id).WhereElementIsNotElementType()
                    .Where<Element>((Func<Element, bool>)(e => e.Category != null && e.Category.Id == category.Id))
                    .Where<Element>((Func<Element, bool>)(e => doc.GetElement(e.GetTypeId()) != null))
                    .Select<Element, Element>((Func<Element, Element>)(e => doc.GetElement(e.GetTypeId())))
                    .Cast<ElementType>().Distinct<ElementType>((IEqualityComparer<ElementType>)new ElementTypeComparer()).ToList<ElementType>())
                    categoryViewModel.ElementTypes.Add(new TypeFilterElementTypeViewModel()
                    {
                        ElementType = elementType,
                        Name = elementType.Name,
                        IsSelected = false
                    });
                categories.Add(categoryViewModel);
            }
            TypeFilterWPF visibilityFilterWpf = new TypeFilterWPF(categories);
            visibilityFilterWpf.ShowDialog();
            if (!visibilityFilterWpf.DialogResult.GetValueOrDefault())
                return Result.Cancelled;
            string btnName = visibilityFilterWpf.BTNName;
            string filterName = visibilityFilterWpf.FilterName;
            string uniqueFilterName = this.GetUniqueFilterName(doc, filterName);
            List<TypeFilterElementTypeViewModel> selectedElementTypes = visibilityFilterWpf.SelectedElementTypes;
            switch (btnName)
            {
                case "btn_Hide":
                    Logger.Log("Скрытие элементов", 1);
                    using (Transaction transaction = new Transaction(doc))
                    {
                        int num1 = (int)transaction.Start("Фильтр");
                        List<ElementId> elementIdSet = new List<ElementId>();
                        foreach (TypeFilterElementTypeViewModel elementTypeViewModel1 in selectedElementTypes)
                        {
                            TypeFilterElementTypeViewModel elementTypeViewModel = elementTypeViewModel1;
                            elementIdSet.AddRange((IEnumerable<ElementId>)new FilteredElementCollector(doc, activeView.Id).WhereElementIsNotElementType().Where<Element>((Func<Element, bool>)(e => e.GetTypeId() == elementTypeViewModel.ElementType.Id)).Select<Element, ElementId>((Func<Element, ElementId>)(e => e.Id)).ToList<ElementId>());
                        }
                        activeView.HideElementsTemporary((ICollection<ElementId>)elementIdSet);
                        doc.Regenerate();
                        int num2 = (int)transaction.Commit();
                        break;
                    }
                case "btn_Isolate":
                    Logger.Log("Изоляция элементов", 1);
                    using (Transaction transaction = new Transaction(doc))
                    {
                        int num3 = (int)transaction.Start("Фильтр");
                        List<ElementId> elementIdList = new List<ElementId>();
                        foreach (TypeFilterElementTypeViewModel elementTypeViewModel2 in selectedElementTypes)
                        {
                            TypeFilterElementTypeViewModel elementTypeViewModel = elementTypeViewModel2;
                            elementIdList.AddRange((IEnumerable<ElementId>)new FilteredElementCollector(doc, activeView.Id).WhereElementIsNotElementType().Where<Element>((Func<Element, bool>)(e => e.GetTypeId() == elementTypeViewModel.ElementType.Id)).Select<Element, ElementId>((Func<Element, ElementId>)(e => e.Id)).ToList<ElementId>());
                        }
                        activeView.IsolateElementsTemporary((ICollection<ElementId>)elementIdList);
                        doc.Regenerate();
                        int num4 = (int)transaction.Commit();
                        break;
                    }
                case "btn_Select":
                    Logger.Log("Выбор элементов", 1);
                    using (Transaction transaction = new Transaction(doc))
                    {
                        int num5 = (int)transaction.Start("Фильтр");
                        List<ElementId> elementIdList = new List<ElementId>();
                        foreach (TypeFilterElementTypeViewModel elementTypeViewModel3 in selectedElementTypes)
                        {
                            TypeFilterElementTypeViewModel elementTypeViewModel = elementTypeViewModel3;
                            elementIdList.AddRange((IEnumerable<ElementId>)new FilteredElementCollector(doc, activeView.Id).WhereElementIsNotElementType().Where<Element>((Func<Element, bool>)(e => e.GetTypeId() == elementTypeViewModel.ElementType.Id)).Select<Element, ElementId>((Func<Element, ElementId>)(e => e.Id)).ToList<ElementId>());
                        }
                        selection.SetElementIds((ICollection<ElementId>)elementIdList);
                        int num6 = (int)transaction.Commit();
                        break;
                    }
                case "btn_CreateFilter":
                    Logger.Log("Сценарий: создать фильтр", 1);
                    try
                    {
                        using (Transaction transaction = new Transaction(doc))
                        {
                            int num7 = (int)transaction.Start("Фильтр");
                            List<ElementId> list2 = selectedElementTypes.Where<TypeFilterElementTypeViewModel>((Func<TypeFilterElementTypeViewModel, bool>)(et => et.ElementType.Category.Id.IntegerValue != -2001352)).Select<TypeFilterElementTypeViewModel, ElementId>((Func<TypeFilterElementTypeViewModel, ElementId>)(et => et.ElementType.Category.Id)).ToList<ElementId>();
                            IList<FilterRule> filterRules = (IList<FilterRule>)new List<FilterRule>();
                            ElementId parameter = new ElementId(BuiltInParameter.SYMBOL_NAME_PARAM);
                            foreach (TypeFilterElementTypeViewModel elementTypeViewModel in selectedElementTypes)
                            {
                                if (elementTypeViewModel.ElementType.Category.Id.IntegerValue != -2001352)
                                    filterRules.Add(ParameterFilterRuleFactory.CreateEqualsRule(parameter, elementTypeViewModel.ElementType.Name, true));
                            }
                            if (filterRules.Count != 0)
                            {
                                ElementFilter filterFromFilterRules = CreateElementFilterFromFilterRules(filterRules);
                                ParameterFilterElement newFilter = ParameterFilterElement.Create(doc, uniqueFilterName, (ICollection<ElementId>)list2, filterFromFilterRules);
                                //проверяем, включен ли шаблон вида и контролирует ли шаблон фильтры
                                bool addFilterToTemplate = false;
                                ElementId templateId = doc.ActiveView.get_Parameter(BuiltInParameter.VIEW_TEMPLATE).AsElementId();
                                if(templateId != null && templateId.IntegerValue != -1)
                                {
                                    //проверяем шаблон вида на предмет отключенной галочки "Фильтры"
                                    ElementId elementId = new ElementId(-1006964); //id отключенной галочки Фильтры (получен опытным путем)
                                    View template = (View)doc.GetElement(templateId);
                                    ICollection<ElementId> elementIds = template.GetNonControlledTemplateParameterIds();
                                    bool filtersDisabled = elementIds.Contains(elementId);
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
                                    doc.ActiveView.AddFilter(newFilter.Id);
                                    doc.ActiveView.SetFilterVisibility(newFilter.Id, false);
                                }
                            }
                            doc.Regenerate();
                            int num8 = (int)transaction.Commit();
                            break;
                        }
                    }
                    catch
                    {
                        int num = (int)TaskDialog.Show("Revit", "Не удалось создать фильтр по выбранным типам!");
                        Logger.Log("Не удалось создать фильтр", 4);
                        return Result.Cancelled;
                    }
            }
            Logger.Log("Завершение работы", 5);
            return Result.Succeeded;
        }

        private string GetUniqueFilterName(Document doc, string filterName)
        {
            int num = 0;
            string filterName1;
            for (filterName1 = filterName; this.FilterNameExists(doc, filterName1); filterName1 = string.Format("{0} ({1})", (object)filterName, (object)num))
                ++num;
            return filterName1;
        }

        private bool FilterNameExists(Document doc, string filterName)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).WhereElementIsNotElementType().ToElements().OfType<ParameterFilterElement>().Any<ParameterFilterElement>((Func<ParameterFilterElement, bool>)(f => f.Name == filterName));
        }

        public static ElementFilter CreateElementFilterFromFilterRules(IList<FilterRule> filterRules)
        {
            IList<ElementFilter> filters = (IList<ElementFilter>)new List<ElementFilter>();
            foreach (FilterRule filterRule in (IEnumerable<FilterRule>)filterRules)
            {
                ElementParameterFilter elementParameterFilter = new ElementParameterFilter(filterRule);
                filters.Add((ElementFilter)elementParameterFilter);
            }
            return (ElementFilter)new LogicalOrFilter(filters);
        }

        
    }
    public class AlphanumComparatorFastString : IComparer<string>
    {
        public int Compare(string s1, string s2)
        {
            if (s1 == null || s2 == null)
                return 0;
            int length1 = s1.Length;
            int length2 = s2.Length;
            int index1 = 0;
            int index2 = 0;
            while (index1 < length1 && index2 < length2)
            {
                char c1 = s1[index1];
                char c2 = s2[index2];
                char[] chArray1 = new char[length1];
                int num1 = 0;
                char[] chArray2 = new char[length2];
                int num2 = 0;
                do
                {
                    chArray1[num1++] = c1;
                    ++index1;
                    if (index1 < length1)
                        c1 = s1[index1];
                    else
                        break;
                }
                while (char.IsDigit(c1) == char.IsDigit(chArray1[0]));
                do
                {
                    chArray2[num2++] = c2;
                    ++index2;
                    if (index2 < length2)
                        c2 = s2[index2];
                    else
                        break;
                }
                while (char.IsDigit(c2) == char.IsDigit(chArray2[0]));
                string s = new string(chArray1);
                string str = new string(chArray2);
                int num3 = !char.IsDigit(chArray1[0]) || !char.IsDigit(chArray2[0]) ? s.CompareTo(str) : int.Parse(s).CompareTo(int.Parse(str));
                if (num3 != 0)
                    return num3;
            }
            return length1 - length2;
        }
    }
}
