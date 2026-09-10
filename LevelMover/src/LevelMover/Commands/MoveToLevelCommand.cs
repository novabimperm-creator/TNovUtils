using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LevelMover.Core;
using LevelMover.UI;
using TNovCommon;

namespace LevelMover.Commands
{
    /// <summary>
    /// Кнопка «Перенести». Работает по текущему выделению: выделил в проекте — нажал — выбрал уровень.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MoveToLevelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uiDocument = uiapp.ActiveUIDocument;
            if (uiDocument == null)
            {
                RevitWindow.ShowDialog(new InfoWindow280("Нет открытой модели."), uiapp);
                return Result.Cancelled;
            }

            Document document = uiDocument.Document;

            ICollection<ElementId> selectedIds = uiDocument.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                RevitWindow.ShowDialog(
                    new InfoWindow280("Сначала выделите элементы в проекте, а затем нажмите «Перенести»."),
                    uiapp);
                return Result.Cancelled;
            }

            List<Element> targets = selectedIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null && !(element is ElementType))
                .ToList();

            if (targets.Count == 0)
            {
                RevitWindow.ShowDialog(new InfoWindow280("В выделении нет элементов модели."), uiapp);
                return Result.Cancelled;
            }

            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToList();

            if (levels.Count == 0)
            {
                RevitWindow.ShowDialog(new InfoWindow280("В проекте нет уровней."), uiapp);
                return Result.Cancelled;
            }

            var picker = new MoveElementsWindow(levels, targets.Count);
            if (RevitWindow.ShowDialog(picker, uiapp) != true) return Result.Cancelled;

            ElementId baseLevelId = picker.BaseLevelId;
            ElementId topLevelId = picker.TopLevelId;

            List<MoveResult> results;
            using (var transaction = new Transaction(document, "Перенос элементов на другой уровень"))
            {
                transaction.Start();
                results = ElementMover.Run(document, targets, baseLevelId, topLevelId);
                transaction.Commit();
            }

            ShowReport(uiapp, uiDocument, results);
            return Result.Succeeded;
        }

        private static void ShowReport(UIApplication uiapp, UIDocument uiDocument, List<MoveResult> results)
        {
            List<MoveResult> skipped = results.Where(r => !r.Moved).ToList();

            if (skipped.Count == 0)
            {
                RevitWindow.ShowDialog(
                    new InfoWindow280(
                        "Перенесено элементов: " + results.Count.ToString(CultureInfo.CurrentCulture) +
                        ". Расположение сохранено."),
                    uiapp);
                return;
            }

            var report = new MoveReportWindow(results);
            if (RevitWindow.ShowDialog(report, uiapp) == true && report.SelectSkipped)
            {
                uiDocument.Selection.SetElementIds(skipped.Select(r => r.Id).ToList());
            }
        }
    }
}
