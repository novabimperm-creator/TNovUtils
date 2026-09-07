using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CleanLinks.Core
{
    /// <summary>Какие наборы связи закрыть, какие открыть.</summary>
    public class WorksetChange
    {
        public List<WorksetId> Close { get; } = new List<WorksetId>();
        public List<WorksetId> Open { get; } = new List<WorksetId>();
    }

    public class WorksetApplyResult
    {
        public int Processed { get; set; }
        public List<string> Failures { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();

        /// <summary>Связи, которые фактически были перезагружены — им повторная перезагрузка не нужна.</summary>
        public List<ElementId> Reloaded { get; } = new List<ElementId>();
    }

    /// <summary>
    /// Открывает и закрывает рабочие наборы внутри RVT-связей. Единственный доступный в API 2022
    /// способ погасить оси конкретной связи: настройки отображения связей на уровне категорий
    /// Autodesk не открыла. Работает через перезагрузку связи с изменённой WorksetConfiguration.
    ///
    /// Наборы адресуются по именам, а не по WorksetId: после перезагрузки связи документ создаётся
    /// заново, и удерживать идентификаторы между попытками нельзя.
    ///
    /// Верить статусу от Revit нельзя — LoadFrom возвращает LinkLoaded и в тех случаях, когда
    /// конфигурацию наборов молча проигнорировал. Каждая попытка проверяется чтением состояния.
    /// </summary>
    public static class LinkWorksetCloser
    {
        /// <summary>
        /// Применяет изменения наборов к связям.
        /// ВАЖНО: вызывать вне транзакции — Revit запрещает перезагрузку связи внутри неё.
        /// </summary>
        public static WorksetApplyResult Apply(Document doc, IDictionary<ElementId, WorksetChange> changes,
            IProgressReporter progress = null)
        {
            var result = new WorksetApplyResult();
            var pathTypesToRestore = new Dictionary<ElementId, PathType>();
            var desiredByLink = new Dictionary<ElementId, HashSet<string>>();
            var namesByLink = new Dictionary<ElementId, string>();

            int index = 0;
            int total = changes.Count;

            foreach (var pair in changes)
            {
                var linkType = doc.GetElement(pair.Key) as RevitLinkType;
                if (linkType == null)
                {
                    result.Failures.Add("Связь не найдена в проекте (id " + pair.Key + ")");
                    continue;
                }

                string linkName = linkType.Name;
                namesByLink[pair.Key] = linkName;

                index++;
                if (progress != null)
                {
                    // Прерывать можно только между связями: перезагрузку Revit не останавливает.
                    if (progress.IsCancelled)
                    {
                        result.Notes.Add("Прервано пользователем: обработано " + (index - 1) + " из " + total);
                        break;
                    }
                    progress.Report(linkName, index, total);
                }

                try
                {
                    Dictionary<int, string> namesById = ReadWorksetNames(doc, pair.Key);

                    HashSet<string> currentlyClosed = ReadClosedNames(doc, pair.Key);
                    var desiredClosed = new HashSet<string>(currentlyClosed);
                    foreach (string name in Resolve(pair.Value.Close, namesById)) desiredClosed.Add(name);
                    foreach (string name in Resolve(pair.Value.Open, namesById)) desiredClosed.Remove(name);

                    if (desiredClosed.SetEquals(currentlyClosed))
                    {
                        result.Notes.Add(linkName + " — наборы уже в нужном состоянии, перезагрузка не нужна");
                        continue;
                    }

                    Diagnostics.Write("  «" + linkName + "» — закрыть: "
                                      + string.Join(", ", desiredClosed.OrderBy(n => n))
                                      + " (сейчас закрыто: "
                                      + (currentlyClosed.Count == 0 ? "ничего" : string.Join(", ", currentlyClosed))
                                      + ")");
                    ReportSessionConflict(doc, pair.Key, linkName);

                    PathType originalPathType = linkType.PathType;
                    desiredByLink[pair.Key] = desiredClosed;

                    if (progress != null)
                    {
                        progress.ReportDetail("закрываю: " + string.Join(", ", desiredClosed.OrderBy(n => n)));
                    }

                    if (!TryAllStrategies(doc, linkType, desiredClosed, result, progress))
                    {
                        DumpWorksets(doc, pair.Key);
                        result.Failures.Add(linkName + " — Revit не применил конфигурацию рабочих наборов "
                                            + "ни перезагрузкой, ни выгрузкой; наборы остались как были");
                        EnsureLoaded(linkType, result);
                        continue;
                    }

                    // Загрузка по абсолютному пути может переключить относительный путь на абсолютный.
                    // Серверные и облачные связи не трогаем — их тип пути задаётся самим хранилищем.
                    if (originalPathType == PathType.Relative)
                    {
                        pathTypesToRestore[pair.Key] = originalPathType;
                    }

                    result.Reloaded.Add(pair.Key);
                    result.Processed++;
                }
                catch (Exception ex)
                {
                    result.Failures.Add(linkName + " — " + ex.Message);
                }
            }

            RestorePathTypes(doc, pathTypesToRestore, result);
            VerifyAfterPathRestore(doc, desiredByLink, namesByLink, result);
            return result;
        }

        /// <summary>
        /// Закрывает наборы связи.
        ///
        /// Форма конфигурации здесь не вопрос вкуса: замеры на реальном проекте показали, что
        /// Revit 2022 молча игнорирует <c>Close()</c> поверх <see cref="WorksetConfigurationOption.OpenAllWorksets"/> —
        /// возвращает LinkLoaded и оставляет все наборы открытыми. Работает только инверсная
        /// форма: закрыть всё и явно открыть нужные. Поэтому единственный рабочий способ и
        /// применяется, а выгрузка остаётся запасным вариантом на случай копии в памяти.
        /// </summary>
        private static bool TryAllStrategies(Document doc, RevitLinkType linkType, HashSet<string> desiredClosed,
            WorksetApplyResult result, IProgressReporter progress)
        {
            if (LoadAndVerify(doc, linkType, desiredClosed, "перезагрузка, CloseAll + Open", result, progress))
            {
                return true;
            }

            // Документ связи может оставаться в памяти, и тогда файл заново не читается.
            Diagnostics.Write("    пробуем через выгрузку связи");
            if (progress != null) progress.ReportDetail("не вышло с первого раза, выгружаю связь");
            linkType.Unload(null);

            return LoadAndVerify(doc, linkType, desiredClosed, "выгрузка + CloseAll + Open", result, progress);
        }

        /// <summary>
        /// Грузит связь с конфигурацией и сразу проверяет, что наборы действительно закрылись.
        /// </summary>
        private static bool LoadAndVerify(Document doc, RevitLinkType linkType, HashSet<string> desiredClosed,
            string attempt, WorksetApplyResult result, IProgressReporter progress)
        {
            ElementId typeId = linkType.Id;
            Dictionary<int, string> namesById = ReadWorksetNames(doc, typeId);

            List<WorksetId> toClose = namesById
                .Where(p => desiredClosed.Contains(p.Value))
                .Select(p => new WorksetId(p.Key))
                .ToList();

            List<WorksetId> toOpen = namesById
                .Where(p => !desiredClosed.Contains(p.Value))
                .Select(p => new WorksetId(p.Key))
                .ToList();

            if (toClose.Count == 0)
            {
                Diagnostics.Write("    " + attempt + ": ни один из наборов не найден по имени — пропуск");
                return false;
            }

            Diagnostics.Write("    " + attempt + ": закрываем id "
                              + string.Join(", ", toClose.Select(w => w.IntegerValue.ToString()))
                              + "; оставляем открытыми " + toOpen.Count);

            if (progress != null) progress.ReportDetail("перезагружаю связь, это может занять время");

            LinkLoadResultType status;
            using (var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets))
            {
                configuration.Open(toOpen);
                status = Reload(linkType, configuration).LoadResult;
            }

            HashSet<string> actual = ReadClosedNames(doc, typeId);
            bool applied = actual.SetEquals(desiredClosed);

            Diagnostics.Write("    " + attempt + ": статус «" + status + "», закрыто после загрузки "
                              + actual.Count + " из ожидаемых " + desiredClosed.Count
                              + (applied ? " — применилось" : " — НЕ применилось"));

            if (status != LinkLoadResultType.LinkLoaded && status != LinkLoadResultType.UsedExisting)
            {
                result.Failures.Add(linkType.Name + " — Revit вернул статус «" + status + "»");
                return false;
            }

            return applied;
        }

        /// <summary>
        /// Если связанный файл открыт в этой же сессии Revit как обычный документ, Revit берёт
        /// копию из памяти и конфигурацию наборов игнорирует. Это видно только так.
        /// </summary>
        private static void ReportSessionConflict(Document doc, ElementId linkTypeId, string linkName)
        {
            try
            {
                string linkPath = GetLinkDocument(doc, linkTypeId)?.PathName;
                if (string.IsNullOrEmpty(linkPath)) return;

                foreach (Document open in doc.Application.Documents)
                {
                    if (open.IsLinked) continue;
                    if (string.Equals(open.PathName, linkPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Diagnostics.Write("    ВНИМАНИЕ: файл связи открыт в этой же сессии Revit — "
                                          + "конфигурация наборов будет проигнорирована");
                        return;
                    }
                }
            }
            catch
            {
                // Диагностика не важнее операции.
            }
        }

        /// <summary>Полный дамп наборов — нужен, когда ни один способ не сработал.</summary>
        private static void DumpWorksets(Document doc, ElementId linkTypeId)
        {
            try
            {
                Document linkDoc = GetLinkDocument(doc, linkTypeId);
                if (linkDoc == null)
                {
                    Diagnostics.Write("    дамп: документ связи недоступен");
                    return;
                }

                Diagnostics.Write("    дамп наборов связи (файл: " + linkDoc.PathName
                                  + ", совместная работа: " + linkDoc.IsWorkshared + "):");

                foreach (Workset workset in new FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset))
                {
                    Diagnostics.Write("      id " + workset.Id.IntegerValue + "  «" + workset.Name
                                      + "»  открыт: " + workset.IsOpen
                                      + ", редактируемый: " + workset.IsEditable
                                      + ", вид: " + workset.Kind);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Write("    дамп не удался: " + ex.Message);
            }
        }

        /// <summary>
        /// Перезагружает связь с заданной конфигурацией рабочих наборов.
        /// Файловые связи (в том числе Revit Server) грузим по ModelPath, облачные — по ссылке ресурса.
        /// </summary>
        private static LinkLoadResult Reload(RevitLinkType linkType, WorksetConfiguration configuration)
        {
            if (linkType.IsExternalFileReference())
            {
                using (ExternalFileReference reference = linkType.GetExternalFileReference())
                {
                    return linkType.LoadFrom(reference.GetAbsolutePath(), configuration);
                }
            }

            ExternalResourceReference cloudReference = linkType
                .GetExternalResourceReferences()
                .Select(p => p.Value)
                .FirstOrDefault(r => r != null);

            if (cloudReference == null)
            {
                throw new InvalidOperationException("у связи нет ссылки, по которой её можно перезагрузить");
            }

            return linkType.LoadFrom(cloudReference, configuration);
        }

        /// <summary>
        /// Мы выгружали связь ради повторной попытки — нельзя оставить её выгруженной,
        /// если попытка не удалась: пользователь просил закрыть наборы, а не убрать связь.
        /// </summary>
        private static void EnsureLoaded(RevitLinkType linkType, WorksetApplyResult result)
        {
            try
            {
                if (linkType.GetLinkedFileStatus() == LinkedFileStatus.Loaded) return;

                linkType.Reload();
                Diagnostics.Write("    связь возвращена в загруженное состояние после неудачной попытки");
            }
            catch (Exception ex)
            {
                result.Failures.Add(linkType.Name + " — связь осталась выгруженной, загрузить обратно "
                                    + "не удалось: " + ex.Message);
            }
        }

        /// <summary>
        /// Возврат относительного пути — тоже правка связи, и Revit может переоткрыть её
        /// с наборами по умолчанию. Проверяем состояние ещё раз уже после него.
        /// </summary>
        private static void VerifyAfterPathRestore(Document doc, Dictionary<ElementId, HashSet<string>> desired,
            Dictionary<ElementId, string> names, WorksetApplyResult result)
        {
            foreach (var pair in desired)
            {
                if (!result.Reloaded.Any(id => id == pair.Key)) continue;

                HashSet<string> actual = ReadClosedNames(doc, pair.Key);
                bool applied = actual.SetEquals(pair.Value);

                string name = names.ContainsKey(pair.Key) ? names[pair.Key] : pair.Key.ToString();
                Diagnostics.Write("    «" + name + "» — после восстановления пути закрыто "
                                  + actual.Count + " из " + pair.Value.Count
                                  + (applied ? " — держится" : " — ОТКАТИЛОСЬ"));

                if (applied) continue;

                result.Failures.Add(name + " — наборы закрылись, но откатились при восстановлении "
                                    + "относительного типа пути");
                result.Processed--;
                result.Reloaded.RemoveAll(id => id == pair.Key);
            }
        }

        private static Document GetLinkDocument(Document doc, ElementId linkTypeId)
        {
            RevitLinkInstance instance = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .FirstOrDefault(i => i.GetTypeId() == linkTypeId);

            return instance?.GetLinkDocument();
        }

        private static Dictionary<int, string> ReadWorksetNames(Document doc, ElementId linkTypeId)
        {
            var names = new Dictionary<int, string>();

            Document linkDoc = GetLinkDocument(doc, linkTypeId);
            if (linkDoc == null || !linkDoc.IsWorkshared) return names;

            foreach (Workset workset in new FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset))
            {
                names[workset.Id.IntegerValue] = workset.Name;
            }

            return names;
        }

        private static HashSet<string> ReadClosedNames(Document doc, ElementId linkTypeId)
        {
            var closed = new HashSet<string>();

            Document linkDoc = GetLinkDocument(doc, linkTypeId);
            if (linkDoc == null || !linkDoc.IsWorkshared) return closed;

            foreach (Workset workset in new FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset))
            {
                if (!workset.IsOpen) closed.Add(workset.Name);
            }

            return closed;
        }

        private static IEnumerable<string> Resolve(IEnumerable<WorksetId> ids, Dictionary<int, string> namesById)
        {
            foreach (WorksetId id in ids)
            {
                string name;
                if (namesById.TryGetValue(id.IntegerValue, out name)) yield return name;
            }
        }

        private static void RestorePathTypes(Document doc, Dictionary<ElementId, PathType> pathTypes,
            WorksetApplyResult result)
        {
            if (pathTypes.Count == 0) return;

            try
            {
                using (var transaction = new Transaction(doc, "Восстановить тип пути связей"))
                {
                    transaction.Start();
                    foreach (var pair in pathTypes)
                    {
                        var linkType = doc.GetElement(pair.Key) as RevitLinkType;
                        if (linkType != null && linkType.PathType != pair.Value)
                        {
                            linkType.PathType = pair.Value;
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                result.Failures.Add("Не удалось вернуть относительный тип пути связей: " + ex.Message);
            }
        }
    }
}
