using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CleanLinks.Core;
using CleanLinks.UI;

namespace CleanLinks.Commands
{
    /// <summary>
    /// Кнопка «Связи проекта»: таблица всех RVT-связей и действий над каждой —
    /// рабочие наборы (в том числе оси), выгрузка и загрузка, графика в текущем виде.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ManageLinksCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                message = "Нет открытого проекта.";
                return Result.Failed;
            }

            Document doc = uiDoc.Document;
            View activeView = uiDoc.ActiveView;

            List<LinkInfo> links = LinkManager.Collect(doc, activeView);
            if (links.Count == 0)
            {
                TaskDialog.Show("Чистые связи", "В проекте нет RVT-связей.");
                return Result.Cancelled;
            }

            IList<LinkPlan> plans;
            using (var window = new LinkManagerForm(links, activeView))
            {
                if (window.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                plans = window.Plans;
            }

            if (plans.All(p => p.IsEmpty))
            {
                return Result.Cancelled;
            }

            try
            {
                ApplyResult result;
                using (var progress = new ProgressForm("Применяю изменения к связям"))
                {
                    progress.ShowOver(commandData.Application.MainWindowHandle);
                    // Транзакцию открывает сам LinkManager: перезагрузка связи внутри неё запрещена.
                    result = LinkManager.Apply(doc, activeView, plans, progress);
                    progress.Finish();
                }

                ShowReport(result);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static void ShowReport(ApplyResult result)
        {
            var lines = new List<string>();
            if (result.WorksetChanged > 0) lines.Add("Рабочие наборы изменены: " + result.WorksetChanged);
            if (result.StateChanged > 0) lines.Add("Связей загружено или выгружено: " + result.StateChanged);
            if (result.ViewChanged > 0) lines.Add("Графика изменена в виде у связей: " + result.ViewChanged);

            var dialog = new TaskDialog("Чистые связи")
            {
                MainInstruction = result.TotalChanged > 0
                    ? "Изменено связей: " + result.TotalChanged
                    : "Ничего не изменилось"
            };

            if (result.Notes.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(result.Notes.Take(10).Select(n => "• " + n));
            }

            if (result.Failures.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("С ошибками: " + result.Failures.Count);
                lines.AddRange(result.Failures.Take(10).Select(f => "• " + f));
                if (result.Failures.Count > 10)
                {
                    lines.Add("… и ещё " + (result.Failures.Count - 10));
                }
            }

            lines.Add(string.Empty);
            lines.Add("Подробности: " + Diagnostics.LogPath);

            dialog.MainContent = string.Join("\n", lines);
            dialog.Show();
        }
    }
}
