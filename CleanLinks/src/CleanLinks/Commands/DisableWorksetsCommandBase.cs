using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CleanLinks.Core;
using CleanLinks.UI;
using TNovCommon;

namespace CleanLinks.Commands
{
    /// <summary>
    /// Общий ход работы всех кнопок «выключить в связях»: собрать связи, узнать у наследника,
    /// какие группы наборов гасить, закрыть их во всех связях сразу и показать отчёт.
    /// Наследники отличаются только тем, как выбираются группы.
    /// </summary>
    public abstract class DisableWorksetsCommandBase : IExternalCommand
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
            List<LinkInfo> links = LinkManager.Collect(doc, uiDoc.ActiveView);

            if (links.Count == 0)
            {
                RevitWindow.ShowDialog(new InfoWindow280("В проекте нет RVT-связей."), uiapp);
                return Result.Cancelled;
            }

            List<WorksetCategory> categories = ChooseCategories(uiapp, links);
            if (categories == null || categories.Count == 0) return Result.Cancelled;

            List<LinkPlan> plans = BuildPlans(links, categories);
            if (plans.Count == 0) return Result.Cancelled;

            var unreachable = links.Where(l => !l.WorksetsAvailable).ToList();
            var withoutMatch = links
                .Where(l => l.WorksetsAvailable && l.WorksetsMatching(categories).Count == 0)
                .ToList();

            Diagnostics.Write("Выключаем группы: " + string.Join(", ", categories.Select(c => c.Name)));

            try
            {
                ApplyResult result;
                var progress = new LinkProgressWindow();
                RevitWindow.Show(progress, uiapp);
                try
                {
                    result = LinkManager.Apply(doc, uiDoc.ActiveView, plans, progress);
                    progress.Finish();
                }
                finally
                {
                    progress.Close();
                }

                ShowReport(uiapp, result, plans.Count, categories, unreachable, withoutMatch);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                RevitWindow.ShowDialog(new InfoWindow280("Не удалось выключить наборы: " + ex.Message), uiapp);
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Какие группы наборов гасить. null или пустой список — пользователь отказался,
        /// команда молча завершается.
        /// </summary>
        protected abstract List<WorksetCategory> ChooseCategories(UIApplication uiapp, List<LinkInfo> links);

        /// <summary>Для каждой пригодной связи — закрыть её наборы выбранных групп, что ещё открыты.</summary>
        protected static List<LinkPlan> BuildPlans(List<LinkInfo> links, List<WorksetCategory> categories)
        {
            var plans = new List<LinkPlan>();

            foreach (LinkInfo link in links.Where(l => l.WorksetsAvailable))
            {
                var plan = new LinkPlan(link);
                foreach (LinkWorksetInfo workset in link.WorksetsMatching(categories))
                {
                    plan.SetDesiredClosed(workset, true);
                }

                // Наборы уже закрыты — связь не трогаем, чтобы не перезагружать её впустую.
                if (plan.HasWorksetChanges) plans.Add(plan);
            }

            return plans;
        }

        private static void ShowReport(UIApplication uiapp, ApplyResult result, int planned, List<WorksetCategory> categories,
            List<LinkInfo> unreachable, List<LinkInfo> withoutMatch)
        {
            var lines = new List<string>
            {
                "Группы: " + string.Join(", ", categories.Select(c => c.Name)) + "."
            };

            if (result.WorksetChanged > 0)
            {
                lines.Add("Закрыто в " + result.WorksetChanged + " связи(ях) из " + planned + ".");
            }

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

            if (withoutMatch.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Подходящего набора нет (" + withoutMatch.Count + "): "
                          + string.Join(", ", withoutMatch.Select(l => l.Name)));
            }

            if (unreachable.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Наборы недоступны (" + unreachable.Count + "):");
                lines.AddRange(unreachable.Select(l => "• " + l.Name + " — " + l.WorksetProblem));
            }

            string head = result.WorksetChanged == planned && result.Failures.Count == 0
                ? "Готово."
                : "Выполнено частично.";
            RevitWindow.ShowDialog(
                new InfoWindow400(head + Environment.NewLine + Environment.NewLine + string.Join("\n", lines)),
                uiapp);
        }
    }

    /// <summary>
    /// Кнопка на одну заранее заданную группу: без окна выбора, только подтверждение с точным
    /// перечнем того, что закроется. Нажатие такой кнопки — уже сам по себе выбор группы.
    /// </summary>
    public abstract class DisableFixedCategoryCommand : DisableWorksetsCommandBase
    {
        /// <summary>Группа наборов, которую гасит эта кнопка.</summary>
        protected abstract WorksetCategory Category { get; }

        /// <summary>Что уточнить в окне подтверждения сверх общего текста. null — ничего.</summary>
        protected virtual string Warning => null;

        protected override List<WorksetCategory> ChooseCategories(UIApplication uiapp, List<LinkInfo> links)
        {
            var categories = new List<WorksetCategory> { Category };
            List<LinkPlan> plans = BuildPlans(links, categories);

            if (plans.Count == 0)
            {
                ShowNothingToDo(uiapp, links, categories);
                return null;
            }

            return Confirm(uiapp, links, categories, plans) ? categories : null;
        }

        private bool Confirm(UIApplication uiapp, List<LinkInfo> links, List<WorksetCategory> categories, List<LinkPlan> plans)
        {
            var window = new TNovUtils.LinkWorksets.ConfirmLinksWindow(
                "Выключить «" + Category.Name + "» во всех связях?",
                string.Join(Environment.NewLine, ConfirmationLines(links, categories, plans)));
            return RevitWindow.ShowDialog(window, uiapp) == true;
        }

        /// <summary>Текст подтверждения. Отдельно от показа окна — так его видно и без Revit.</summary>
        private List<string> ConfirmationLines(List<LinkInfo> links, List<WorksetCategory> categories,
            List<LinkPlan> plans)
        {
            int worksets = plans.Sum(p => p.WorksetsToClose.Count);

            var lines = new List<string>
            {
                "Будет закрыто наборов: " + worksets + " в " + plans.Count + " связи(ях).",
                string.Empty
            };

            lines.AddRange(plans.Take(12).Select(DescribePlan));
            if (plans.Count > 12) lines.Add("… и ещё " + (plans.Count - 12));

            AppendUntouched(lines, links, categories);

            lines.Add(string.Empty);
            lines.Add("Действует на весь проект, включая 3D и разрезы. Связи при этом перезагружаются.");
            if (Warning != null) lines.Add(Warning);

            return lines;
        }

        private static string DescribePlan(LinkPlan plan)
        {
            var names = plan.Link.Worksets
                .Where(w => w.IsOpen && plan.WillBeClosed(w))
                .Select(w => w.Name);

            return "• " + plan.Link.Name + " — " + string.Join(", ", names);
        }

        /// <summary>
        /// Связи, которые останутся нетронутыми, перечисляем прямо в подтверждении: иначе
        /// список читается как полный охват проекта, а это не так.
        /// </summary>
        private static void AppendUntouched(List<string> lines, List<LinkInfo> links,
            List<WorksetCategory> categories)
        {
            var untouched = new List<string>();

            foreach (LinkInfo link in links)
            {
                if (!link.WorksetsAvailable)
                {
                    untouched.Add("• " + link.Name + " — " + link.WorksetProblem);
                }
                else if (link.WorksetsMatching(categories).Count == 0)
                {
                    untouched.Add("• " + link.Name + " — подходящего набора нет");
                }
                else if (link.WorksetsMatching(categories).All(w => !w.IsOpen))
                {
                    untouched.Add("• " + link.Name + " — уже закрыты");
                }
            }

            if (untouched.Count == 0) return;

            lines.Add(string.Empty);
            lines.Add("Не затронуто (" + untouched.Count + "):");
            lines.AddRange(untouched.Take(12));
            if (untouched.Count > 12) lines.Add("… и ещё " + (untouched.Count - 12));
        }

        private void ShowNothingToDo(UIApplication uiapp, List<LinkInfo> links, List<WorksetCategory> categories)
        {
            var lines = new List<string>
            {
                "Подходящие наборы либо не найдены, либо уже закрыты."
            };

            AppendUntouched(lines, links, categories);
            RevitWindow.ShowDialog(
                new InfoWindow400("Закрывать нечего." + Environment.NewLine + Environment.NewLine + string.Join("\n", lines)),
                uiapp);
        }
    }
}
