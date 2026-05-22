using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using Autodesk.Revit.Creation;
using System.Xml.Linq;
using Autodesk.Revit.DB.Architecture;
using System.Linq;
using System.Security.Cryptography;
using TNovCommon;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    [RegenerationAttribute(RegenerationOption.Manual)]

    public class Paint : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string TNovClassName = "Краска+"; DateTime dateTime = DateTime.Now; string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Autodesk.Revit.DB.Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            
            //проверка подключения, запись в журнал
            if(ServerUtils.CheckConnection(TNovClassName, TNovVersion)==false) return Result.Failed;

            // создание log - файла
            Logger.Initialize(TNovClassName,dateTime,TNovVersion);
            

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

            //Транзакция           

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
                        Logger.Log("Прервано: " + e.Message + " .Завершение работы.", 3);
                        break;
                    }
                }

                
            }
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
