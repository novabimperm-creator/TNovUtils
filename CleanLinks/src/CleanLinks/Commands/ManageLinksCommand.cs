using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CleanLinks.Core;
using CleanLinks.UI;
using TNovCommon;

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
            UIApplication uiapp = commandData.Application;
            UIDocument uiDoc = uiapp.ActiveUIDocument;
            if (uiDoc == null)
            {
                RevitWindow.ShowDialog(new InfoWindow280("Нет открытой модели."), uiapp);
                return Result.Cancelled;
            }

            Document doc = uiDoc.Document;
            View activeView = uiDoc.ActiveView;

            List<LinkInfo> links = LinkManager.Collect(doc, activeView);
            if (links.Count == 0)
            {
                RevitWindow.ShowDialog(new InfoWindow280("В проекте нет RVT-связей."), uiapp);
                return Result.Cancelled;
            }

            var window = new LinkManagerWindow(links, activeView);
            if (RevitWindow.ShowDialog(window, uiapp) != true)
                return Result.Cancelled;

            IList<LinkPlan> plans = window.Plans;
            if (plans.All(p => p.IsEmpty))
                return Result.Cancelled;

            try
            {
                ApplyResult result;
                var progress = new LinkProgressWindow();
                RevitWindow.Show(progress, uiapp);
                try
                {
                    result = LinkManager.Apply(doc, activeView, plans, progress);
                    progress.Finish();
                }
                finally
                {
                    progress.Close();
                }

                ShowReport(uiapp, result);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                RevitWindow.ShowDialog(new InfoWindow280("Не удалось применить изменения: " + ex.Message), uiapp);
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static void ShowReport(UIApplication uiapp, ApplyResult result)
        {
            var lines = new List<string>();
            if (result.WorksetChanged > 0) lines.Add("Рабочие наборы изменены: " + result.WorksetChanged);
            if (result.StateChanged > 0) lines.Add("Связей загружено или выгружено: " + result.StateChanged);
            if (result.ViewChanged > 0) lines.Add("Графика изменена в виде у связей: " + result.ViewChanged);

            string head = result.TotalChanged > 0
                ? "Изменено связей: " + result.TotalChanged
                : "Ничего не изменилось";

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
                    lines.Add("… и ещё " + (result.Failures.Count - 10));
            }

            string body = lines.Count == 0 ? head : head + Environment.NewLine + Environment.NewLine + string.Join("\n", lines);
            RevitWindow.ShowDialog(new InfoWindow400(body), uiapp);
        }
    }
}
