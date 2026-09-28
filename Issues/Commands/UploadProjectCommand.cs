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
    /// «Загрузить проект в TNovPRO» — один раз выгрузить дом целиком (сводный файл
    /// и выбранные связи) во вкладку «Модель» сайта. Дальше сайт обновляется сам
    /// при каждой синхронизации (<see cref="ModelSyncService"/>).
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
            TNovConfigLoad.LoadConfig("Загрузить проект в TNovPRO",
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString());

            var document = ModelExporter.DocumentName(doc);
            if (document.IndexOf("отсоединено", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                new InfoWindow400("Файл открыт «отсоединённым» от центральной модели — синхронизации с сайтом не будет.\n" +
                                  "Откройте сводный файл как локальную копию центральной модели.").ShowDialog();
                return Result.Cancelled;
            }

            // Вход — тот же, что у «Вопросов». Без него дальше идти бессмысленно:
            // дом выгружается минутами, а отправить его будет некуда.
            var session = IssuesHost.Session;
            if (!RunBlocking(() => session.TryRestoreSessionAsync(), out bool signedIn) || !signedIn)
            {
                new InfoWindow400("Нет входа в TNovPRO. Откройте «TNovPRO Вопросы», войдите и повторите.").ShowDialog();
                return Result.Cancelled;
            }

            // Имя дома — как у выгрузки для Атласа: шифр до первого «_».
            var modelName = document.Split('_')[0].Trim();

            try
            {
                var links = ModelLinks.Collect(doc);
                var picker = new LinkPickerWindow(doc.Title, links, uiapp.MainWindowHandle);
                if (picker.ShowDialog() != true) return Result.Cancelled;
                var chosen = picker.Chosen;

                var confirm = new TaskDialog("Загрузить проект в TNovPRO")
                {
                    MainInstruction = $"Загрузить «{modelName}» во вкладку «Модель»?",
                    MainContent = $"Разделы: {ModelLinks.SectionOf(document)}"
                                  + (chosen.Count > 0 ? ", " + string.Join(", ", chosen.Select(c => c.Section)) : "") + ".\n\n"
                                  + "Дом выгружается целиком — на большом проекте это минуты. "
                                  + "Дальше сайт обновляется сам при каждой синхронизации с центральной моделью.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.Yes,
                };
                if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;

                var report = new StringBuilder();
                var clock = Stopwatch.StartNew();
                var window = new ExportProgressWindow("Загрузка проекта в TNovPRO", uiapp.MainWindowHandle);
                ModelExporter.ExportResult result;
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

                    result = ModelExporter.ExportProject(doc, int.MaxValue, ModelExporter.ExportProfile.Ask, say, progress, ids);
                    if (result.Cancelled)
                    {
                        TryDelete(result.GlbFile);
                        return Result.Cancelled;
                    }

                    // Отправка — в фоне, окно при этом живёт и показывает ход.
                    string file = result.GlbFile;
                    var upload = Task.Run(async () =>
                    {
                        var uploadId = await session.Client.UploadChunkedAsync(file,
                            (done, total) => window.Dispatcher.BeginInvoke(new Action(() =>
                                window.SetStage("отправка на сайт", done, total))));
                        await session.Client.ImportModelAsync(uploadId, modelName);
                    });
                    while (!upload.IsCompleted)
                    {
                        window.Pump();
                        Thread.Sleep(50);
                    }
                    TryDelete(file);
                    upload.GetAwaiter().GetResult();   // ошибка отправки — в общий catch
                }
                finally
                {
                    window.ForceClose();
                }
                clock.Stop();

                // Разделы начинаем вести сразу, не дожидаясь, пока плагин обновит список.
                ModelSyncService.MarkLoaded(document);
                foreach (var c in chosen) ModelSyncService.MarkLoaded(Path.GetFileNameWithoutExtension(c.FileName));

                new TaskDialog("Загрузить проект в TNovPRO")
                {
                    MainInstruction = "Отправлено — сайт раскладывает модель",
                    MainContent = $"«{modelName}»: {result.PropertyCount:N0} элементов, {result.GlbLength / 1024.0 / 1024.0:N1} МБ, "
                                  + $"{clock.Elapsed.TotalMinutes:N1} мин.\n"
                                  + "Через пару минут модель появится на сайте: «Проекты» → «Модель».\n\n" + report,
                }.Show();
                return Result.Succeeded;
            }
            catch (AuthRequiredException)
            {
                new InfoWindow400("Вход в TNovPRO истёк. Откройте «TNovPRO Вопросы», войдите и повторите.").ShowDialog();
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

        private static bool RunBlocking<T>(Func<Task<T>> work, out T value)
        {
            value = default(T);
            try { value = Task.Run(work).GetAwaiter().GetResult(); return true; }
            catch (Exception ex) { PluginLog.Write("[UploadProject] вход: " + ex.Message); return false; }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
