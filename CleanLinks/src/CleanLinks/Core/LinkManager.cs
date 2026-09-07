using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CleanLinks.Core
{
    /// <summary>
    /// Читает состояние всех RVT-связей проекта и применяет к ним намеченные действия.
    ///
    /// Порядок применения важен: графика вида требует транзакции, а перезагрузка и
    /// выгрузка связи внутри транзакции запрещены. Поэтому сначала идёт транзакция с
    /// переопределениями вида, затем всё остальное вне транзакции.
    /// </summary>
    public static class LinkManager
    {
        public static List<LinkInfo> Collect(Document doc, View view)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var result = new List<LinkInfo>();

            var instancesByType = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .GroupBy(i => i.GetTypeId())
                .ToDictionary(g => g.Key, g => g.ToList());

            bool viewSupportsOverrides = ViewSupportsOverrides(view);

            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .OrderBy(t => t.Name, StringComparer.CurrentCulture)
                .ToList();

            foreach (RevitLinkType linkType in linkTypes)
            {
                var info = new LinkInfo
                {
                    TypeId = linkType.Id,
                    Name = linkType.Name,
                    Status = linkType.GetLinkedFileStatus(),
                    IsNested = linkType.IsNestedLink,
                    PathType = linkType.PathType
                };
                info.IsLoaded = info.Status == LinkedFileStatus.Loaded;
                info.CanReload = linkType.IsExternalFileReference() || linkType.RefersToExternalResourceReferences();

                result.Add(info);

                List<RevitLinkInstance> instances;
                if (instancesByType.TryGetValue(linkType.Id, out instances))
                {
                    info.InstanceIds.AddRange(instances.Select(i => i.Id));
                }

                FillViewState(info, doc, view, viewSupportsOverrides);
                FillWorksets(info, linkType, instances);
            }

            Diagnostics.StartSession("Менеджер связей — чтение");
            foreach (LinkInfo info in result)
            {
                Diagnostics.Write("  «" + info.Name + "» — " + info.StatusText
                                  + ", экземпляров: " + info.InstanceIds.Count
                                  + ", наборы: " + (info.WorksetsAvailable
                                      ? info.Worksets.Count + " (закрыто " + info.Worksets.Count(w => !w.IsOpen) + ")"
                                      : info.WorksetProblem));
            }

            return result;
        }

        private static bool ViewSupportsOverrides(View view)
        {
            if (view == null || view.IsTemplate) return false;
            try
            {
                return view.AreGraphicsOverridesAllowed();
            }
            catch
            {
                return false;
            }
        }

        private static void FillViewState(LinkInfo info, Document doc, View view, bool viewSupportsOverrides)
        {
            if (view == null)
            {
                info.ViewProblem = "нет активного вида";
                return;
            }
            if (!viewSupportsOverrides)
            {
                info.ViewProblem = "активный вид не поддерживает переопределения графики";
                return;
            }
            if (info.InstanceIds.Count == 0)
            {
                info.ViewProblem = info.IsNested
                    ? "вложенная связь — экземпляром управляет родительский файл"
                    : "у связи нет экземпляра в проекте";
                return;
            }

            try
            {
                ElementId first = info.InstanceIds[0];
                info.Halftone = view.GetElementOverrides(first).Halftone;
                info.HiddenInView = doc.GetElement(first).IsHidden(view);
            }
            catch (Exception ex)
            {
                info.ViewProblem = "не удалось прочитать графику связи: " + ex.Message;
            }
        }

        private static void FillWorksets(LinkInfo info, RevitLinkType linkType, List<RevitLinkInstance> instances)
        {
            if (info.IsNested)
            {
                info.WorksetProblem = "вложенная связь — управляется из родительского файла";
                return;
            }
            if (!info.IsLoaded)
            {
                info.WorksetProblem = "связь не загружена — наборы прочитать нельзя";
                return;
            }

            // IsFromLocalPath() здесь не годится: связи с Revit Server (RSN://) отдают false,
            // хотя LoadFrom(ModelPath, WorksetConfiguration) с серверным путём работает.
            if (!info.CanReload)
            {
                info.WorksetProblem = "у связи нет внешней ссылки — перезагрузка не поддерживается";
                return;
            }

            if (instances == null || instances.Count == 0)
            {
                info.WorksetProblem = "у связи нет экземпляра в проекте";
                return;
            }

            Document linkDoc = instances[0].GetLinkDocument();
            if (linkDoc == null)
            {
                info.WorksetProblem = "документ связи недоступен";
                return;
            }
            if (!linkDoc.IsWorkshared)
            {
                info.WorksetProblem = "в связанном файле нет совместной работы — рабочих наборов нет";
                return;
            }

            foreach (Workset workset in new FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset))
            {
                info.Worksets.Add(new LinkWorksetInfo
                {
                    Id = workset.Id,
                    Name = workset.Name,
                    IsOpen = workset.IsOpen
                });
            }

            info.Worksets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

            if (info.Worksets.Count == 0)
            {
                info.WorksetProblem = "пользовательских рабочих наборов не найдено";
            }
        }

        /// <summary>
        /// Применяет планы. Вызывать вне транзакции: перезагрузка и выгрузка связи внутри неё запрещены.
        /// </summary>
        public static ApplyResult Apply(Document doc, View view, IList<LinkPlan> plans,
            IProgressReporter progress = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (plans == null) throw new ArgumentNullException(nameof(plans));

            var result = new ApplyResult();
            List<LinkPlan> active = plans.Where(p => !p.IsEmpty).ToList();
            if (active.Count == 0) return result;

            Diagnostics.StartSession("Менеджер связей — применение");

            // Выгрузка обнуляет смысл возни с наборами: связи в проекте всё равно не будет.
            foreach (LinkPlan plan in active.Where(p => p.Action == LinkStateAction.Unload && p.HasWorksetChanges))
            {
                result.Notes.Add(plan.Link.Name + " — связь выгружается, изменения рабочих наборов пропущены");
                plan.WorksetsToClose.Clear();
                plan.WorksetsToOpen.Clear();
            }

            ApplyViewGraphics(doc, view, active, result);
            List<ElementId> reloaded = ApplyWorksets(doc, active, result, progress);
            ApplyStateActions(doc, active, reloaded, result);

            Diagnostics.Write("Итого: вид " + result.ViewChanged
                              + ", наборы " + result.WorksetChanged
                              + ", состояние " + result.StateChanged
                              + ", ошибок " + result.Failures.Count);
            return result;
        }

        private static void ApplyViewGraphics(Document doc, View view, List<LinkPlan> plans, ApplyResult result)
        {
            List<LinkPlan> withViewChanges = plans.Where(p => p.HasViewChanges).ToList();
            if (withViewChanges.Count == 0) return;

            if (view == null)
            {
                foreach (LinkPlan plan in withViewChanges)
                {
                    result.Failures.Add(plan.Link.Name + " — нет активного вида, графику менять негде");
                }
                return;
            }

            using (var transaction = new Transaction(doc, "Чистые связи: графика связей в виде"))
            {
                transaction.Start();

                foreach (LinkPlan plan in withViewChanges)
                {
                    try
                    {
                        ApplyViewGraphicsToLink(doc, view, plan);
                        Diagnostics.Write("  «" + plan.Link.Name + "» — полутон: " + plan.Halftone
                                          + ", скрытие: " + plan.HideInView + " в виде «" + view.Name + "»");
                        result.ViewChanged++;
                    }
                    catch (Exception ex)
                    {
                        result.Failures.Add(plan.Link.Name + " — графика в виде: " + ex.Message);
                    }
                }

                transaction.Commit();
            }
        }

        private static void ApplyViewGraphicsToLink(Document doc, View view, LinkPlan plan)
        {
            if (plan.Halftone != TriState.Unchanged)
            {
                bool halftone = plan.Halftone == TriState.On;
                foreach (ElementId id in plan.Link.InstanceIds)
                {
                    OverrideGraphicSettings overrides = view.GetElementOverrides(id);
                    overrides.SetHalftone(halftone);
                    view.SetElementOverrides(id, overrides);
                }
            }

            if (plan.HideInView == TriState.Unchanged) return;

            // Revit ругается, если прятать уже спрятанное или показывать невидимое,
            // поэтому отбираем только те экземпляры, состояние которых реально меняется.
            bool hide = plan.HideInView == TriState.On;
            List<ElementId> targets = plan.Link.InstanceIds
                .Where(id => IsHidden(doc, id, view) != hide)
                .ToList();

            if (targets.Count == 0) return;

            if (hide) view.HideElements(targets);
            else view.UnhideElements(targets);
        }

        private static bool IsHidden(Document doc, ElementId id, View view)
        {
            try
            {
                Element element = doc.GetElement(id);
                return element != null && element.IsHidden(view);
            }
            catch
            {
                return false;
            }
        }

        private static List<ElementId> ApplyWorksets(Document doc, List<LinkPlan> plans, ApplyResult result,
            IProgressReporter progress)
        {
            var changes = new Dictionary<ElementId, WorksetChange>();

            foreach (LinkPlan plan in plans.Where(p => p.HasWorksetChanges))
            {
                var change = new WorksetChange();
                change.Close.AddRange(plan.WorksetsToClose);
                change.Open.AddRange(plan.WorksetsToOpen);
                changes[plan.TypeId] = change;
            }

            if (changes.Count == 0) return new List<ElementId>();

            WorksetApplyResult worksetResult = LinkWorksetCloser.Apply(doc, changes, progress);
            result.WorksetChanged += worksetResult.Processed;
            result.Failures.AddRange(worksetResult.Failures);
            result.Notes.AddRange(worksetResult.Notes);
            return worksetResult.Reloaded;
        }

        private static void ApplyStateActions(Document doc, List<LinkPlan> plans, List<ElementId> reloaded,
            ApplyResult result)
        {
            foreach (LinkPlan plan in plans.Where(p => p.Action != LinkStateAction.None))
            {
                var linkType = doc.GetElement(plan.TypeId) as RevitLinkType;
                if (linkType == null)
                {
                    result.Failures.Add(plan.Link.Name + " — связь не найдена в проекте");
                    continue;
                }

                // Смена наборов уже перезагрузила связь — второй раз незачем.
                if (plan.Action == LinkStateAction.Reload
                    && reloaded.Any(id => id == plan.TypeId))
                {
                    result.Notes.Add(plan.Link.Name + " — перезагружена при смене рабочих наборов");
                    continue;
                }

                try
                {
                    switch (plan.Action)
                    {
                        case LinkStateAction.Unload:
                            linkType.Unload(null);
                            break;
                        case LinkStateAction.Load:
                            CheckLoad(linkType.Load(), plan.Link.Name);
                            break;
                        case LinkStateAction.Reload:
                            CheckLoad(linkType.Reload(), plan.Link.Name);
                            break;
                    }

                    Diagnostics.Write("  «" + plan.Link.Name + "» — действие: " + plan.Action);
                    result.StateChanged++;
                }
                catch (Exception ex)
                {
                    result.Failures.Add(plan.Link.Name + " — " + plan.Action + ": " + ex.Message);
                }
            }
        }

        private static void CheckLoad(LinkLoadResult loadResult, string linkName)
        {
            if (loadResult.LoadResult == LinkLoadResultType.LinkLoaded
                || loadResult.LoadResult == LinkLoadResultType.UsedExisting)
            {
                return;
            }

            throw new InvalidOperationException("Revit вернул статус «" + loadResult.LoadResult + "»");
        }
    }
}
