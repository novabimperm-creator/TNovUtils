using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using TNovUtils.Issues.Revit;
using TNovUtils.Issues.UI;

namespace TNovUtils.Issues.Commands
{
    /// <summary>
    /// Открывает немодальное окно «Вопросы» (замечания TNovPRO) для текущей модели.
    /// Имя модели нормализуется здесь (валидный API-контекст), затем передаётся в окно.
    /// Кнопка ленты объявляется в TNov/Application.cs:
    ///   new PushButtonData(nameof(ShowIssuesCommand), "Вопросы\nTNovPRO",
    ///       typeof(ShowIssuesCommand).Assembly.Location, typeof(ShowIssuesCommand).FullName)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ShowIssuesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // В экосистеме TNov нет нашего IExternalApplication (кнопки объявляет TNov),
            // поэтому ExternalEvent-мост инициализируем лениво здесь — Execute даёт
            // валидный API-контекст. Initialize идемпотентен.
            RevitBridge.Initialize();
            if (RevitAPI.UiApplication == null) RevitAPI.Initialize(commandData);

            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
            {
                new InfoWindow280("Нет открытой модели.").ShowDialog();
                return Result.Cancelled;
            }

            var modelName = ModelNameResolver.Resolve(uidoc.Document, uiapp.Application, out bool detached);
            if (detached)
            {
                new InfoWindow400(
                    "Файл открыт «отсоединённым» от центральной модели — привязывать замечания не к чему.\n" +
                    "Откройте файл как локальную копию центральной модели.").ShowDialog();
                return Result.Cancelled;
            }

            try
            {
                // Немодальное окно: singleton, владелец — главное окно Revit.
                IssuesHost.ShowOrActivate(uiapp, modelName);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
