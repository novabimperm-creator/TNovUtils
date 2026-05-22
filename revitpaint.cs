using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System;
using TNovCommon;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    public class revitpaint : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = RevitAPI.UiApplication;
            
            RevitCommandId id_built_in = RevitCommandId.LookupPostableCommandId(PostableCommand.Paint);
            uiApp.PostCommand(id_built_in);

            return Result.Succeeded;
        }
    }
}
