using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI.Selection;
using System;
using TNovCommon;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    [RegenerationAttribute(RegenerationOption.Manual)]

    public class Paint2 : IExternalCommand
    {
       
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string TNovClassName = "Материал?"; DateTime dateTime = DateTime.Now; string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            
            //проверка подключения, запись в журнал
            if(ServerUtils.CheckConnection(TNovClassName, TNovVersion)==false) return Result.Failed;

            // создание log - файла
            Logger.Initialize(TNovClassName,dateTime,TNovVersion);
            

            Logger.Log("Выбор грани", 1);
            Selection selection = uidoc.Selection;

            Reference faceRef = null;

            try
            {
                faceRef = selection.PickObject(ObjectType.Face, "Выберите исходную грань (Esc - отмена)");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException e)
            {
                Logger.Log("Отменено: " + e.Message + " .Завершение работы.", 3); return Result.Cancelled;
            }

            GeometryObject geoObject = doc.GetElement(faceRef).GetGeometryObjectFromReference(faceRef);

            PlanarFace planarFace = geoObject as PlanarFace;

            ElementId matId = planarFace.MaterialElementId;

            Element mat = doc.GetElement(matId);

            string txt = mat.Name;

            Logger.Log("Материал " + matId.ToString()+ " - "+ txt,1);
            Logger.Log("Диалоговое окно",1);

            // Диалоговое окно
            var viewModel = new InfoWindowTextFieldViewModel();
            viewModel.headtxt = "Элементу/грани назначен следующий материал:";
            viewModel.ids = txt;
            viewModel.lowtxt = "Вы можете найти его в Диспетчере материалов.";
            var wpfview = new InfoWindowTextField(viewModel);
            bool? ok = wpfview.ShowDialog();

            Logger.Log("Завершение работы.",5);
            return Result.Succeeded;
        }
    }
}
