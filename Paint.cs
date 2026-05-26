using Autodesk.Revit.Attributes;
using Autodesk.Revit.Creation;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using TNovCommon;
using Document = Autodesk.Revit.DB.Document;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    [RegenerationAttribute(RegenerationOption.Manual)]

    public class Paint : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Краска+";
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

            #region Выбор грани

            Logger.Log("Выбор исходной грани",1);
            Selection selection = uidoc.Selection;

            Reference faceRef = null;

            try
            {
                faceRef = selection.PickObject(ObjectType.Face, "Выберите исходную грань (Esc - отмена)");
                //Edge
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException e)
            {
                Logger.Log("Отменено: "+e.Message+" .Завершение работы.",3); return Result.Cancelled;
            }

            GeometryObject geoObject = doc.GetElement(faceRef).GetGeometryObjectFromReference(faceRef);

            PlanarFace planarFace = geoObject as PlanarFace;

            ElementId matId = planarFace.MaterialElementId;

            Element mat = doc.GetElement(matId);

            Logger.Log("Материал " + matId.ToString() + " - " + mat.Name,1);

            SurfaceSelectionFilter surfaceSelectionFilter = new SurfaceSelectionFilter();
            PaintableSelectionFilter paintableSelectionFilter = new PaintableSelectionFilter();
            #endregion

            #region Основной код           

            using (Transaction transaction = new Transaction(doc))
            {
                

                
                while (true)
                {
                    
                    try
                    {
                        transaction.Start("TNov - краска+");
                        Logger.Log("Открываем транзакцию",1);
                        Reference ref2 = selection.PickObject((ObjectType)2, (ISelectionFilter)surfaceSelectionFilter, "Выберите грани (Esc - отмена)");
                        if (ref2 != null)
                        {
                            GeometryObject objFromRef2 = doc.GetElement(ref2).GetGeometryObjectFromReference(ref2);
                            string name = doc.GetElement(ref2).Name;
                            Face face = objFromRef2 as Face;
                            Element element = doc.GetElement(ref2);
                            Logger.Log("   элемент " + element.Id.ToString(),1);
                            doc.Paint(element.Id, face, matId);
                            /*GeometryObject geoObject1 = doc.GetElement(geoFace).GetGeometryObjectFromReference(geoFace);
                            PlanarFace planarFace1 = geoObject1 as PlanarFace;
                            ElementId eId = doc.GetElement(geoFace).Id;

                            Logger.Log("   элемент "+eId.ToString());
                            doc.Paint(eId, planarFace1, matId);*/
                            transaction.Commit();
                            Logger.Log("Закрываем транзакцию",1);
                            
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException e)
                    {
                        break;
                    }
                }

                
            }

            #endregion
            Logger.Log("Завершение работы.",5);
            return Result.Succeeded;
        }
        
        

    }
    public class SurfaceSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element element) => element.Category.Id.IntegerValue != -2001352;

        public bool AllowReference(Reference refer, XYZ point) => refer.ElementReferenceType.ToString() == "REFERENCE_TYPE_SURFACE";
    }
    public class PaintableSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element element) => element.Category.Id.IntegerValue != -2001352;

        public bool AllowReference(Reference refer, XYZ point) => refer.ElementReferenceType.ToString() == "REFERENCE_TYPE_SURFACE";
    }
}
