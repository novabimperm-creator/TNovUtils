using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using TNovUtils.Issues.Api;
using TNovUtils.Issues.ModelSync;
using TNovUtils.Issues.Revit;
using TNovUtils.Issues.UI;

namespace TNovUtils.Issues.Commands
{
    /// <summary>
    /// «TNovPRO Модель» — ОДНА кнопка на дом (решение Виктора 2026-10-21): один раз
    /// выгрузить дом целиком (сводный файл и выбранные связи) в «Модель» сайта.
    /// Дальше сайт обновляется сам по синхронизациям (<see cref="ModelSyncService"/>),
    /// а из «Модели» сервер сам собирает Атлас для браузера и считает смету Афины —
    /// отдельных кнопок выгрузки больше нет.
    ///
    /// Вход в TNovPRO — здесь же, через браузер: ждём под окном с «Отмена», чтобы
    /// Revit не «висел на авторизации» (у руководителя 10.2026 вход висел: ожидание
    /// сети шло на потоке Revit без окна).
    ///
    /// Кнопка ленты объявляется в TNov/Application.cs рядом с «Вопросами».
    /// </summary>
    // Manual: отмеченную, но выгруженную связь загружаем сами (RevitLinkType.Load).
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class UploadProjectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null)
            {
                new InfoWindow280("Нет открытой модели.").ShowDialog();
                return Result.Cancelled;
            }
            if (RevitAPI.UiApplication == null) RevitAPI.Initialize(data);
            if (!CorpGate.Require(TNovConfigLoad.GetCachedConfig())) return Result.Cancelled;
            TNovConfigLoad.LoadConfig("Загрузить проект в TNovPRO",
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString());

            var document = ModelExporter.DocumentName(doc);
            if (document.IndexOf("отсоединено", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                new InfoWindow400("Файл открыт «отсоединённым» от центральной модели — синхронизации с сайтом не будет.\n" +
                                  "Откройте сводный файл как локальную копию центральной модели.").ShowDialog();
                return Result.Cancelled;
            }

            // Вход — тот же, что у «Вопросов», но прямо отсюда. Без него дальше идти
            // бессмысленно: дом выгружается минутами, а отправить его будет некуда.
            var session = IssuesHost.Session;
            var signedIn = SignIn(session, uiapp.MainWindowHandle);
            if (signedIn == null) return Result.Cancelled;
            if (signedIn == false)
            {
                new InfoWindow400("Войти в TNovPRO не удалось. Проверьте связь с сайтом и повторите — " +
                                  "вход откроется в браузере.").ShowDialog();
                return Result.Cancelled;
            }

            // Имя дома — шифр до первого «_».
            var modelName = document.Split('_')[0].Trim();

            try
            {
                var links = ModelLinks.Collect(doc);
                var picker = new LinkPickerWindow(doc.Title, links, uiapp.MainWindowHandle);
                if (picker.ShowDialog() != true) return Result.Cancelled;
                var chosen = picker.Chosen;

                var confirm = new TaskDialog("Загрузить проект в TNovPRO")
                {
                    MainInstruction = $"Загрузить «{modelName}» в «Модель»?",
                    MainContent = $"Разделы: {ModelLinks.SectionOf(document)}"
                                  + (chosen.Count > 0 ? ", " + string.Join(", ", chosen.Select(c => c.Section)) : "") + ".\n\n"
                                  + "Дом выгружается и отправляется по разделам — на большом проекте это минуты. "
                                  + "Дальше сайт обновляется сам: через час после последней синхронизации с центральной моделью. "
                                  + "Из «Модели» собираются Атлас и смета Афины — отдельно их выгружать не нужно.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.Yes,
                };
                if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;

                var report = new StringBuilder();
                var clock = Stopwatch.StartNew();
                var window = new ExportProgressWindow("Загрузка проекта в TNovPRO", uiapp.MainWindowHandle);
                int sections = 0, elementsTotal = 0;
                bool cancelled = false;
                try
                {
                    window.Show();
                    var progress = new ExportProgress
                    {
                        Status = text => window.SetStage(text),
                        CancelRequested = () => window.CancelRequested,
                    };
                    Action<string> say = line => { report.AppendLine(line); PluginLog.Write("[UploadProject] " + line); };

                    foreach (var link in chosen)
                    {
                        if (link.Loaded) continue;
                        window.SetStage($"загружаю связь {link.FileName}…");
                        window.Pump();
                        var failure = ModelLinks.Load(doc, link);
                        say(failure == null ? $"связь загружена: {link.FileName}" : $"⚠ не удалось загрузить связь {link.FileName}: {failure}");
                    }
                    var wanted = new HashSet<string>(chosen.Select(c => c.FileName), StringComparer.OrdinalIgnoreCase);
                    var ids = ModelLinks.Collect(doc).Where(l => wanted.Contains(l.FileName)).Select(l => l.InstanceId).ToList();

                    // По разделам: выгрузили раздел → отправили → удалили временные файлы → следующий.
                    // Весь дом ни в Revit, ни на сервере целиком в памяти не бывает.
                    foreach (var part in ModelExporter.ExportProjectSections(doc, say, progress, ids))
                    {
                        try
                        {
                            if (part.Cancelled) { cancelled = true; break; }
                            var send = Task.Run(async () =>
                            {
                                var geomId = part.WithGeometry > 0
                                    ? await session.Client.UploadChunkedAsync(part.GlbFile, (done, total) =>
                                        window.Dispatcher.BeginInvoke(new Action(() => window.SetStage($"{part.Section}: отправка геометрии", done, total))))
                                    : null;
                                var passId = await session.Client.UploadChunkedAsync(part.PassportsFile, (done, total) =>
                                    window.Dispatcher.BeginInvoke(new Action(() => window.SetStage($"{part.Section}: отправка паспортов", done, total))));
                                await session.Client.ImportSectionAsync(modelName, part.Section, part.Document, part.Matrix, geomId, passId);
                            });
                            while (!send.IsCompleted)
                            {
                                window.Pump();
                                Thread.Sleep(50);
                            }
                            send.GetAwaiter().GetResult();   // ошибка отправки — в общий catch
                            sections++;
                            elementsTotal += part.Elements;
                            // Раздел начинаем вести сразу, не дожидаясь, пока плагин обновит список.
                            ModelSyncService.MarkLoaded(part.Document);
                            say($"{part.Section}: отправлен ({part.Document})");
                        }
                        finally
                        {
                            part.DeleteFiles();
                        }
                        if (window.CancelRequested) { cancelled = true; break; }
                    }
                }
                finally
                {
                    window.ForceClose();
                }
                clock.Stop();

                new TaskDialog("Загрузить проект в TNovPRO")
                {
                    MainInstruction = cancelled
                        ? $"Прервано — отправлено разделов: {sections}"
                        : "Отправлено — сайт раскладывает модель",
                    MainContent = $"«{modelName}»: разделов {sections}, элементов {elementsTotal:N0}, {clock.Elapsed.TotalMinutes:N1} мин.\n"
                                  + "Через пару минут модель появится на сайте («Модель»), Атлас соберётся следом.\n\n" + report,
                }.Show();
                return Result.Succeeded;
            }
            catch (AuthRequiredException)
            {
                new InfoWindow400("Вход в TNovPRO истёк. Нажмите кнопку ещё раз — вход откроется в браузере.").ShowDialog();
                return Result.Cancelled;
            }
            catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                new InfoWindow400("Загружать проекты на сайт может BIM-отдел — доступ выдаёт администратор TNovPRO.").ShowDialog();
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                PluginLog.Write("[UploadProject] FAIL: " + ex);
                message = "Не удалось загрузить проект: " + ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Войти в TNovPRO: сначала тихо по сохранённому входу, иначе — через браузер.
        /// Ожидание — под окном с «Отмена», очередь сообщений крутится: поток Revit
        /// не блокируется ни на сети, ни на человеке, который ищет окно браузера.
        /// true — вошли; false — не вышло; null — человек отменил.
        /// </summary>
        private static bool? SignIn(ApiSession session, IntPtr revitWindow)
        {
            var window = new ExportProgressWindow("Вход в TNovPRO", revitWindow, "Отмена");
            try
            {
                window.SetStage("Проверяю вход в TNovPRO…");
                window.Show();
                var restore = Task.Run(() => session.TryRestoreSessionAsync());
                if (!Wait(window, restore)) return null;
                if (restore.Status == TaskStatus.RanToCompletion && restore.Result) return true;
                if (restore.IsFaulted) PluginLog.Write("[UploadProject] вход (restore): " + restore.Exception?.GetBaseException().Message);

                window.SetStage("Открылся браузер — войдите в TNovPRO и вернитесь в Revit…");
                var browser = Task.Run(() => session.Browser.AuthorizeAsync(TimeSpan.FromMinutes(3)));
                if (!Wait(window, browser)) return null;
                if (browser.IsFaulted) PluginLog.Write("[UploadProject] вход (browser): " + browser.Exception?.GetBaseException().Message);
                return browser.Status == TaskStatus.RanToCompletion && browser.Result;
            }
            finally
            {
                window.ForceClose();
            }
        }

        /// <summary>Крутить окно, пока задача не кончится. false — нажали «Отмена».</summary>
        private static bool Wait(ExportProgressWindow window, Task task)
        {
            while (!task.IsCompleted)
            {
                window.Pump();
                if (window.CancelRequested) return false;
                Thread.Sleep(50);
            }
            return true;
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
