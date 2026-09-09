using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LevelMover.Core;
using LevelMover.UI;

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
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null)
            {
                TaskDialog.Show("Перенос элементов", "Откройте проект.");
                return Result.Cancelled;
            }

            Document document = uiDocument.Document;

            ICollection<ElementId> selectedIds = uiDocument.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("Перенос элементов",
                    "Сначала выделите элементы в проекте, а затем нажмите «Перенести».");
                return Result.Cancelled;
            }

            // Типоразмеры попадают в выделение через диспетчер проекта, а уровня у них нет —
            // отчёт из-за них разбухал бы пустыми строками.
            List<Element> targets = selectedIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null && !(element is ElementType))
                .ToList();

            if (targets.Count == 0)
            {
                TaskDialog.Show("Перенос элементов", "В выделении нет элементов модели.");
                return Result.Cancelled;
            }

            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToList();

            if (levels.Count == 0)
            {
                TaskDialog.Show("Перенос элементов", "В проекте нет уровней.");
                return Result.Cancelled;
            }

            ElementId baseLevelId;
            ElementId topLevelId;

            using (var form = new MoveElementsForm(levels, targets.Count))
            {
                if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;

                baseLevelId = form.BaseLevelId;
                topLevelId = form.TopLevelId;
            }

            List<MoveResult> results;
            using (var transaction = new Transaction(document, "Перенос элементов на другой уровень"))
            {
                transaction.Start();
                results = ElementMover.Run(document, targets, baseLevelId, topLevelId);
                transaction.Commit();
            }

            ShowReport(uiDocument, results);
            return Result.Succeeded;
        }

        private static void ShowReport(UIDocument uiDocument, List<MoveResult> results)
        {
            List<MoveResult> skipped = results.Where(r => !r.Moved).ToList();

            if (skipped.Count == 0)
            {
                TaskDialog.Show("Перенос элементов",
                    "Перенесено элементов: " + results.Count.ToString(CultureInfo.CurrentCulture) +
                    ". Расположение сохранено.");
                return;
            }

            using (var report = new ReportForm(results))
            {
                report.ShowDialog();

                // Выделение переносим на непереехавшие: иначе их пришлось бы искать по ID вручную.
                if (report.SelectSkipped)
                {
                    uiDocument.Selection.SetElementIds(skipped.Select(r => r.Id).ToList());
                }
            }
        }
    }
}
