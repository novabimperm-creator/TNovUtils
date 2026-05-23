using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System.Linq;
using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using TNovCommon;

namespace TNovUtils
{


    

    [Transaction(TransactionMode.Manual)] //важно чтобы после этой строчки был IExternalCommand!
    public class IdSelection : IExternalCommand
    {
        
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Выбор по ID";
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            string docName = doc.Title.ToString(); docName = docName.Replace(",", " ");
            string userName = rvtApp.Username; userName = userName.Replace(",", "");
            string docNameUserName = "_" + userName; docName = docName.Replace(docNameUserName, "");
            docName = docName.Replace(",", "");
            #endregion

            TNovConfig config = TNovConfigLoad.LoadConfig(DBCommandName, TNovVersion);

            #region Настройки логов
            // создание log - файла
            Logger.Initialize(DBCommandName, dateTime, TNovVersion);

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
            #endregion
            
#region Диалог
            Logger.Log("Открываем диалоговое окно",1);
            var viewModel = new IdSelectionViewModel();
            // Десериализация
            bool forProject = false;
            json js = new json(in TNovClassName, in forProject, out bool canserialize, out string jsonpath);
            if (canserialize) 
            {
                viewModel = JsonConvert.DeserializeObject<IdSelectionViewModel>(File.ReadAllText(jsonpath));
                Logger.Log("Десериализация прошла успешно",1);
            }
            var wpfview = new IdSelectionWPF(viewModel);
            viewModel.CloseRequest += (s, e) => wpfview.Close();
            bool? ok = wpfview.ShowDialog();
            if (ok != null && ok == true) { } 
            else { Logger.Log("Запуск отменен пользователем. Завершение работы.", 3); return Result.Cancelled; }
            //Сериализация
            try
            {
                File.WriteAllText(jsonpath, JsonConvert.SerializeObject(viewModel));
                Logger.Log("Сериализация прошла успешно",1);
            }
            catch (Exception ex) { Logger.Log("Ошибка при сериализации: "+ex.Message,4); }
            
            bool runIt = false;
            string ids = viewModel.elemids;
            bool isolate = viewModel.isolate;
            bool cutview = viewModel.cut;
            
            string[] s_ids = ids.Split(',');
#endregion

#region Основной код
            Logger.Log("Завершаем изоляцию вида", 1);
            string viewtype = RevitAPI.UiDocument.ActiveGraphicalView.Title;
            Autodesk.Revit.DB.View3D view3d;

            List<View> views = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)   //фильтр по категории Виды
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<View>()                     //элементы категории Виды
                                                                         .ToList();                         //формируем список
            string userName = rvtApp.Username;
            bool dws = doc.IsWorkshared;
            string viewName = "{3D}";
            if (dws)
            {
                viewName = "{3D - " + userName + "}";

            }
            foreach (View view in views)
            {
                if (view.Name == viewName&&cutview) { uidoc.ActiveView = view; break; }
            }

            // Восстанавливаем вид
            using (Transaction trans1 = new Transaction(doc))
            {
                Logger.Log("Открываем транзакцию 1 - завершить изоляцию вида", 1);
                trans1.Start("TNov - завершить изоляцию вида");
                uidoc.ActiveGraphicalView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                if (viewtype.Contains("3D")) { view3d = (View3D)RevitAPI.UiDocument.ActiveGraphicalView; }
                trans1.Commit();
                Logger.Log("Закрываем транзакцию 1", 1);
            }

            // Выделяем элементы
            uidoc.Selection.SetElementIds(ids.Split(',').Select(s => new ElementId(int.Parse(s))).ToArray());
            Logger.Log("Элементы "+ids+" выделены",1);

            // BoundingBox введенных элементов
            List<BoundingBoxXYZ> boxes = new List<BoundingBoxXYZ>(); //пустой список Bbox

            foreach (string id in s_ids)
            {
                int idint = Convert.ToInt32(id);
                ElementId eid = new ElementId(idint);
                Element elem = doc.GetElement(eid);
                Logger.Log("Элемент "+eid.ToString()+". Получаем Bbox",1);
                BoundingBoxXYZ el_box = elem.get_BoundingBox(doc.ActiveView);
                boxes.Add(el_box);
            }
            if (isolate && cutview && viewtype.Contains("3D")) { runIt = true; }
            else if (isolate) { runIt = true; }
            else if (cutview && viewtype.Contains("3D")) { runIt = true; }

            if (runIt)
            {
                using (Transaction trans2 = new Transaction(doc))
                {
                    if (isolate && cutview && viewtype.Contains("3D"))
                    {
                        Logger.Log("Открываем транзакцию 2 - изолировать элементы и подрезать 3D-вид",1);
                        try{
trans2.Start("TNov - изолировать элементы и подрезать 3D-вид");
                        uidoc.ActiveGraphicalView.IsolateElementsTemporary(ids.Split(',').Select(s => new ElementId(int.Parse(s))).ToArray());
                        view3d = (View3D)RevitAPI.UiDocument.ActiveGraphicalView;
                        var bb = boxes.Aggregate((acc, elem) => acc._BbUnion(elem)); //объединение Bbox (ссылка на метод класса BbUnion) 
                        BoundingBoxXYZ expandedBox = new BoundingBoxXYZ
                        {
                            Min = new XYZ(
            bb.Min.X - 1,
            bb.Min.Y - 1,
            bb.Min.Z - 1
        ),
                            Max = new XYZ(
            bb.Max.X + 1,
            bb.Max.Y + 1,
            bb.Max.Z + 1
        )
                        };
                        view3d.SetSectionBox(expandedBox);
                        trans2.Commit();
                        Logger.Log("Закрываем транзакцию 2",1);
} 
catch (Exception ex)
                   		{
                        Logger.Log("Ошибка: " + ex.Message, 4);
                   		}
                    }
                    else if (isolate)
                    {
                        Logger.Log("Открываем транзакцию 2 - изолировать элементы",1);
                        try{
trans2.Start("TNov - изолировать элементы");
                        RevitAPI.UiDocument.ActiveGraphicalView.IsolateElementsTemporary(ids.Split(',').Select(s => new ElementId(int.Parse(s))).ToArray());
                        trans2.Commit();
                        Logger.Log("Закрываем транзакцию 2", 1);
} 
catch (Exception ex)
                    {
                        Logger.Log("Ошибка: " + ex.Message, 4);
                     }
                    }
                    else if (cutview && viewtype.Contains("3D"))
                    {
                        Logger.Log("Открываем транзакцию 2 - подрезать 3D-вид",1);
                        try{
trans2.Start("TNov - подрезать 3D-вид");
                        view3d = (View3D)RevitAPI.UiDocument.ActiveGraphicalView;
                        var bb = boxes.Aggregate((acc, elem) => acc._BbUnion(elem)); //объединение Bbox (ссылка на метод класса BbUnion) 
                        BoundingBoxXYZ expandedBox = new BoundingBoxXYZ
                        {
                            Min = new XYZ(
            bb.Min.X - 1,
            bb.Min.Y - 1,
            bb.Min.Z - 1
        ),
                            Max = new XYZ(
            bb.Max.X + 1,
            bb.Max.Y + 1,
            bb.Max.Z + 1
        )
                        };
                        view3d.SetSectionBox(expandedBox);
                        trans2.Commit();
                        Logger.Log("Закрываем транзакцию 2",1);
} 
catch (Exception ex)
                    {
                        Logger.Log("Ошибка: " + ex.Message, 4);
                     }
                    }
                }
            }
#endregion


            Logger.Log("Завершение работы.",5);

            return Result.Succeeded;
        }

        
    }

}
