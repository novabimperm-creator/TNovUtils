using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using TNovUtils.Checklist.Revit;

namespace TNovUtils.Checklist.Commands
{
    /// <summary>
    /// Открывает немодальное окно «Чек-лист».
    /// Кнопка ленты объявляется в TNov/Application.cs:
    ///   new PushButtonData(nameof(ShowChecklistCommand), "Чек-лист",
    ///       typeof(ShowChecklistCommand).Assembly.Location, typeof(ShowChecklistCommand).FullName)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ShowChecklistCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ChecklistRevitBridge.Initialize();
            if (RevitAPI.UiApplication == null) RevitAPI.Initialize(commandData);

            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
            {
                new InfoWindow280("Нет открытой модели.").ShowDialog();
                return Result.Cancelled;
            }

            if (string.IsNullOrEmpty(uidoc.Document.PathName))
            {
                new InfoWindow280("Сначала сохраните проект, чтобы хранить результаты проверок.").ShowDialog();
                return Result.Cancelled;
            }

            try
            {
                ChecklistHost.ShowOrActivate(uiapp);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = Flatten(ex);
                return Result.Failed;
            }
        }

        private static string Flatten(Exception ex)
        {
            var text = ex.Message;
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                text += " → " + inner.Message;
            return text;
        }
    }
}
