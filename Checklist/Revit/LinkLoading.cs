using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtils.Checklist.Revit
{
    /// <summary>Загруженная связь: документ, его положение в модели и имя типа связи для лога.</summary>
    internal sealed class LoadedLink
    {
        public Document Document;
        public Transform Transform;
        public string Name;
    }

    /// <summary>
    /// Открытие рабочего набора связи и загрузка связи — общее для автопроверок, которым нужны связи.
    /// </summary>
    internal static class LinkLoading
    {
        /// <summary>
        /// Открыть закрытый набор API не умеет; обход — ShowElements по элементу набора (только с UI).
        /// Окна «Нет открытого вида… Продолжить?» и «Не удалось подобрать вид» закрываются сами:
        /// поиск вида не нужен, набор открывается и без него.
        /// </summary>
        public static bool TryOpenWorkset(Document doc, Element element)
        {
            var uiApp = RevitAPI.UiApplication ?? new UIApplication(doc.Application);
            uiApp.DialogBoxShowing += DismissDialog;
            try
            {
                new UIDocument(doc).ShowElements(element.Id);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("Чек-лист: не удалось открыть набор через ShowElements: " + ex.Message, 3);
                return false;
            }
            finally
            {
                uiApp.DialogBoxShowing -= DismissDialog;
            }
        }

        /// <summary>Отмена поиска вида, остальные окна — «Закрыть»/«ОК».</summary>
        private static void DismissDialog(object sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e)
        {
            try
            {
                bool dismissed = e.OverrideResult((int)TaskDialogResult.Cancel)
                                 || e.OverrideResult((int)TaskDialogResult.Close)
                                 || e.OverrideResult((int)TaskDialogResult.Ok);
                Logger.Log($"Чек-лист: при открытии набора связи {(dismissed ? "закрыто" : "не удалось закрыть")} окно {e.DialogId}", 2);
            }
            catch (Exception ex)
            {
                Logger.Log("Чек-лист: ошибка закрытия окна при открытии набора связи: " + ex.Message, 3);
            }
        }

        /// <returns>null — связь загружена (или уже была загружена), иначе текст ошибки.</returns>
        public static string TryLoad(Document doc, RevitLinkType type)
        {
            if (RevitLinkType.IsLoaded(doc, type.Id)) return null;
            try
            {
                var result = type.Load().LoadResult;
                return result == LinkLoadResultType.LinkLoaded || result == LinkLoadResultType.UsedExisting
                    ? null
                    : "Revit вернул статус «" + result + "»";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Экземпляры связей верхнего уровня, в имени которых есть маркер. Закрытый набор связи открывается
        /// (только с UI), после этого связь загружается. Выгруженную пользователем связь не загружаем.
        /// Всё, что не удалось проверить, пишется в лог.
        /// </summary>
        /// <param name="label">Разделы связей для лога, например «ЭЛ/СС/ПС».</param>
        /// <param name="loadUnloaded">Загружать и выгруженную пользователем связь (загруженную не перезагружаем).</param>
        /// <param name="exclude">Связи с этой подстрокой в имени пропускаются.</param>
        public static List<LoadedLink> CollectLoaded(Document doc, string[] markers, string label, bool allowUi, StringBuilder log,
            bool loadUnloaded = false, string exclude = null)
        {
            var links = new List<LoadedLink>();
            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .Where(t => !t.IsNestedLink && Report.ChecklistCatalog.ContainsAny(t.Name, markers)
                            && (exclude == null || t.Name.IndexOf(exclude, StringComparison.Ordinal) < 0))
                .ToList();
            if (linkTypes.Count == 0)
            {
                log.AppendLine();
                log.AppendLine($"Связи {label} ({string.Join(", ", markers)}) не найдены");
                return links;
            }

            var allInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();
            var table = doc.IsWorkshared ? doc.GetWorksetTable() : null;

            foreach (var type in linkTypes)
            {
                var instances = allInstances.Where(i => i.GetTypeId() == type.Id).ToList();
                if (instances.Count == 0)
                {
                    log.AppendLine();
                    log.AppendLine($"Экземпляр связи {type.Name} не размещён в модели");
                    continue;
                }

                bool openedWorkset = false;
                if (table != null)
                {
                    foreach (var inst in instances)
                    {
                        var workset = table.GetWorkset(inst.WorksetId);
                        if (workset == null || workset.IsOpen) continue;
                        if (allowUi && TryOpenWorkset(doc, inst) && table.GetWorkset(inst.WorksetId).IsOpen)
                        {
                            openedWorkset = true;
                            log.AppendLine();
                            log.AppendLine($"Рабочий набор «{workset.Name}» связи {type.Name} был закрыт и открыт автоматически");
                        }
                        else
                        {
                            log.AppendLine();
                            log.AppendLine($"Рабочий набор «{workset.Name}» связи {type.Name} закрыт — связь не проверена, откройте набор");
                        }
                    }
                }

                // Выгруженную пользователем связь не загружаем (кроме loadUnloaded); загружаем после открытия её набора
                if (!RevitLinkType.IsLoaded(doc, type.Id))
                {
                    string error = openedWorkset || loadUnloaded ? TryLoad(doc, type) : "связь выгружена";
                    if (error != null)
                    {
                        log.AppendLine();
                        log.AppendLine($"Связь {type.Name} не проверена: {error}");
                        continue;
                    }
                }

                foreach (var inst in instances)
                {
                    var linkDoc = inst.GetLinkDocument();
                    if (linkDoc == null) continue;
                    links.Add(new LoadedLink { Document = linkDoc, Transform = inst.GetTotalTransform(), Name = type.Name });
                }
            }
            return links;
        }
    }
}
