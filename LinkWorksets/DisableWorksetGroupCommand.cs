using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtils.LinkWorksets
{
    /// <summary>
    /// Общий ход работы кнопок «выключить в связях»: собрать связи, показать одно окно
    /// подтверждения с точным перечнем наборов, закрыть их во всех связях сразу, показать отчёт.
    /// Наследники задают только группу наборов.
    ///
    /// Окна выбора нет намеренно: нажатие кнопки — это уже сам выбор группы.
    /// </summary>
    public abstract class DisableWorksetGroupCommand : IExternalCommand
    {
        /// <summary>Группа наборов, которую гасит кнопка.</summary>
        protected abstract WorksetGroup Group { get; }

        /// <summary>Имя для лога.</summary>
        protected abstract string CommandName { get; }

        /// <summary>Что уточнить в окне подтверждения сверх общего текста. null — ничего.</summary>
        protected virtual string Warning => null;

        /// <summary>Сколько связей перечислять поимённо, чтобы окно не разрасталось.</summary>
        private const int MaxListed = 8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (RevitAPI.UiApplication == null) RevitAPI.Initialize(commandData);

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                new InfoWindow280("Нет открытой модели.").ShowDialog();
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            Logger.Initialize(CommandName, DateTime.Now, version);
            Logger.Log("Выключение группы «" + Group.Name + "» во всех RVT-связях", 0);

            try
            {
                List<LinkInfo> links = LinkScanner.Collect(doc);
                if (links.Count == 0)
                {
                    Logger.Log("В проекте нет RVT-связей. Завершение работы.", 3);
                    new InfoWindow280("В проекте нет RVT-связей.").ShowDialog();
                    return Result.Cancelled;
                }

                List<CloseRequest> requests = BuildRequests(links);
                if (requests.Count == 0)
                {
                    Logger.Log("Закрывать нечего. Завершение работы.", 3);
                    new InfoWindow400("Закрывать нечего: подходящие наборы либо не найдены, либо уже закрыты."
                                      + Environment.NewLine + Environment.NewLine
                                      + Untouched(links)).ShowDialog();
                    return Result.Cancelled;
                }

                if (!Confirm(links, requests))
                {
                    Logger.Log("Запуск отменен пользователем. Завершение работы.", 3);
                    return Result.Cancelled;
                }

                // Вне транзакции: Revit запрещает перезагрузку связи внутри неё.
                CloseResult result = LinkWorksetCloser.Apply(doc, requests);

                Logger.Log("Закрыто в " + result.Changed + " связи(ях) из " + requests.Count
                           + ", ошибок: " + result.Failures.Count, 5);
                new InfoWindow400(Report(links, requests, result)).ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка: " + ex.Message, 4);
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Для каждой пригодной связи — закрыть её наборы группы, что ещё открыты.</summary>
        private List<CloseRequest> BuildRequests(List<LinkInfo> links)
        {
            var requests = new List<CloseRequest>();

            foreach (LinkInfo link in links.Where(l => l.Available))
            {
                // Уже закрытые не считаем: иначе связь пришлось бы перезагружать впустую.
                List<LinkWorkset> toClose = link.Matching(Group).Where(w => w.IsOpen).ToList();
                if (toClose.Count == 0) continue;

                requests.Add(new CloseRequest
                {
                    TypeId = link.TypeId,
                    LinkName = link.Name,
                    WorksetNames = new HashSet<string>(toClose.Select(w => w.Name))
                });
            }

            return requests;
        }

        private bool Confirm(List<LinkInfo> links, List<CloseRequest> requests)
        {
            int worksets = requests.Sum(r => r.WorksetNames.Count);

            var lines = new List<string>
            {
                "Будет закрыто наборов: " + worksets + " в " + requests.Count + " связи(ях).",
                string.Empty
            };

            lines.AddRange(requests
                .Take(MaxListed)
                .Select(r => "• " + r.LinkName + " — " + string.Join(", ", r.WorksetNames.OrderBy(n => n))));

            if (requests.Count > MaxListed) lines.Add("… и ещё " + (requests.Count - MaxListed));

            string untouched = Untouched(links);
            if (untouched != null)
            {
                lines.Add(string.Empty);
                lines.Add(untouched);
            }

            lines.Add(string.Empty);
            lines.Add("Действует на весь проект, включая 3D и разрезы. Связи при этом перезагружаются.");
            if (Warning != null) lines.Add(Warning);

            var viewModel = new QuestionWindowViewModel
            {
                // Имя группы как есть, в кавычках: приведённое к нижнему регистру «арматура»
                // не согласуется по падежу с «выключить».
                headtxt = "Выключить «" + Group.Name + "» во всех связях?" + Environment.NewLine
                          + Environment.NewLine + string.Join(Environment.NewLine, lines)
            };

            var window = new QuestionWindow280(viewModel);
            viewModel.CloseRequest += (s, e) => window.Close();
            bool? answer = window.ShowDialog();
            return answer == true;
        }

        /// <summary>
        /// Связи, которые останутся нетронутыми, и почему. Молчание про них читается
        /// как «сделано везде», а это неправда. null — таких связей нет.
        /// </summary>
        private string Untouched(List<LinkInfo> links)
        {
            var lines = new List<string>();

            foreach (LinkInfo link in links)
            {
                if (!link.Available)
                {
                    lines.Add("• " + link.Name + " — " + link.Problem);
                }
                else
                {
                    List<LinkWorkset> matching = link.Matching(Group);
                    if (matching.Count == 0) lines.Add("• " + link.Name + " — подходящего набора нет");
                    else if (matching.All(w => !w.IsOpen)) lines.Add("• " + link.Name + " — уже закрыты");
                }
            }

            if (lines.Count == 0) return null;

            var text = new List<string> { "Не затронуто (" + lines.Count + "):" };
            text.AddRange(lines.Take(MaxListed));
            if (lines.Count > MaxListed) text.Add("… и ещё " + (lines.Count - MaxListed));

            return string.Join(Environment.NewLine, text);
        }

        private string Report(List<LinkInfo> links, List<CloseRequest> requests, CloseResult result)
        {
            var lines = new List<string>
            {
                result.Changed == requests.Count && result.Failures.Count == 0 ? "Готово." : "Выполнено частично.",
                string.Empty,
                "Группа: " + Group.Name + ".",
                "Закрыто в " + result.Changed + " связи(ях) из " + requests.Count + "."
            };

            if (result.Notes.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(result.Notes.Take(MaxListed).Select(n => "• " + n));
            }

            if (result.Failures.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("С ошибками: " + result.Failures.Count);
                lines.AddRange(result.Failures.Take(MaxListed).Select(f => "• " + f));
                if (result.Failures.Count > MaxListed)
                {
                    lines.Add("… и ещё " + (result.Failures.Count - MaxListed));
                }
            }

            string untouched = Untouched(links);
            if (untouched != null)
            {
                lines.Add(string.Empty);
                lines.Add(untouched);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
