using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TNovCommon;

namespace TNovUtils.LinkWorksets
{
    /// <summary>Какие наборы связи требуется закрыть. Наборы адресуются по именам.</summary>
    public sealed class CloseRequest
    {
        public ElementId TypeId { get; set; }
        public string LinkName { get; set; }
        public HashSet<string> WorksetNames { get; set; }
    }

    public sealed class CloseResult
    {
        /// <summary>Связей, где наборы действительно закрылись.</summary>
        public int Changed { get; set; }

        public List<string> Failures { get; } = new List<string>();

        /// <summary>Сделано не так, как просили, но без ошибки.</summary>
        public List<string> Notes { get; } = new List<string>();

        public List<ElementId> Reloaded { get; } = new List<ElementId>();
    }

    /// <summary>
    /// Закрывает рабочие наборы внутри RVT-связей через перезагрузку связи с изменённой
    /// WorksetConfiguration.
    ///
    /// Наборы адресуются по именам, а не по WorksetId: после перезагрузки документ связи
    /// создаётся заново, и удерживать идентификаторы между попытками нельзя.
    ///
    /// Верить статусу от Revit нельзя — LoadFrom возвращает LinkLoaded и в тех случаях, когда
    /// конфигурацию наборов молча проигнорировал. Каждая попытка проверяется чтением состояния.
    /// </summary>
    public static class LinkWorksetCloser
    {
        /// <summary>
        /// ВАЖНО: вызывать вне транзакции — Revit запрещает перезагрузку связи внутри неё.
        /// </summary>
        public static CloseResult Apply(Document doc, IList<CloseRequest> requests)
        {
            var result = new CloseResult();
            var pathTypesToRestore = new Dictionary<ElementId, PathType>();
            var desiredByLink = new Dictionary<ElementId, HashSet<string>>();
            var namesByLink = new Dictionary<ElementId, string>();

            foreach (CloseRequest request in requests)
            {
                var linkType = doc.GetElement(request.TypeId) as RevitLinkType;
                if (linkType == null)
                {
                    result.Failures.Add(request.LinkName + " — связь не найдена в проекте");
                    continue;
                }

                string linkName = linkType.Name;
                namesByLink[request.TypeId] = linkName;

                try
                {
                    HashSet<string> currentlyClosed = ReadClosedNames(doc, request.TypeId);
                    var desiredClosed = new HashSet<string>(currentlyClosed);
                    foreach (string name in request.WorksetNames) desiredClosed.Add(name);

                    if (desiredClosed.SetEquals(currentlyClosed))
                    {
                        result.Notes.Add(linkName + " — наборы уже закрыты, перезагрузка не нужна");
                        continue;
                    }

                    Logger.Log("«" + linkName + "» — закрыть: "
                               + string.Join(", ", desiredClosed.OrderBy(n => n))
                               + " (сейчас закрыто: "
                               + (currentlyClosed.Count == 0 ? "ничего" : string.Join(", ", currentlyClosed))
                               + ")", 2);
                    ReportSessionConflict(doc, request.TypeId, linkName);

                    PathType originalPathType = linkType.PathType;
                    desiredByLink[request.TypeId] = desiredClosed;

                    if (!TryAllStrategies(doc, linkType, desiredClosed, result))
                    {
                        DumpWorksets(doc, request.TypeId);
                        result.Failures.Add(linkName + " — Revit не применил конфигурацию рабочих наборов "
                                            + "ни перезагрузкой, ни выгрузкой; наборы остались как были");
                        EnsureLoaded(linkType, result);
                        continue;
                    }

                    // Загрузка по абсолютному пути может переключить относительный путь на абсолютный.
                    // Серверные и облачные связи не трогаем — их тип пути задаётся самим хранилищем.
                    if (originalPathType == PathType.Relative)
                    {
                        pathTypesToRestore[request.TypeId] = originalPathType;
                    }

                    result.Reloaded.Add(request.TypeId);
                    result.Changed++;
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
        /// Форма конфигурации здесь не вопрос вкуса: замеры на реальном проекте показали, что
        /// Revit 2022 молча игнорирует <c>Close()</c> поверх <see cref="WorksetConfigurationOption.OpenAllWorksets"/> —
        /// возвращает LinkLoaded и оставляет все наборы открытыми. Работает только инверсная
        /// форма: закрыть всё и явно открыть нужные. Выгрузка остаётся запасным вариантом на
        /// случай, когда документ связи держится в памяти и файл заново не читается.
        /// </summary>
        private static bool TryAllStrategies(Document doc, RevitLinkType linkType, HashSet<string> desiredClosed,
            CloseResult result)
        {
            if (LoadAndVerify(doc, linkType, desiredClosed, "перезагрузка, CloseAll + Open", result))
            {
                return true;
            }

            Logger.Log("  пробуем через выгрузку связи", 2);
            linkType.Unload(null);

            return LoadAndVerify(doc, linkType, desiredClosed, "выгрузка + CloseAll + Open", result);
        }

        /// <summary>Грузит связь с конфигурацией и сразу проверяет, что наборы действительно закрылись.</summary>
        private static bool LoadAndVerify(Document doc, RevitLinkType linkType, HashSet<string> desiredClosed,
            string attempt, CloseResult result)
        {
            ElementId typeId = linkType.Id;

            List<KeyValuePair<WorksetId, string>> worksets = ReadWorksets(doc, typeId);

            List<WorksetId> toClose = worksets
                .Where(p => desiredClosed.Contains(p.Value))
                .Select(p => p.Key)
                .ToList();

            List<WorksetId> toOpen = worksets
                .Where(p => !desiredClosed.Contains(p.Value))
                .Select(p => p.Key)
                .ToList();

            if (toClose.Count == 0)
            {
                Logger.Log("  " + attempt + ": ни один из наборов не найден по имени — пропуск", 2);
                return false;
            }

            Logger.Log("  " + attempt + ": закрываем " + toClose.Count
                       + ", оставляем открытыми " + toOpen.Count, 2);

            LinkLoadResultType status;
            using (var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets))
            {
                configuration.Open(toOpen);
                status = Reload(linkType, configuration).LoadResult;
            }

            HashSet<string> actual = ReadClosedNames(doc, typeId);
            bool applied = actual.SetEquals(desiredClosed);

            Logger.Log("  " + attempt + ": статус «" + status + "», закрыто после загрузки "
                       + actual.Count + " из ожидаемых " + desiredClosed.Count
                       + (applied ? " — применилось" : " — НЕ применилось"), 2);

            if (status != LinkLoadResultType.LinkLoaded && status != LinkLoadResultType.UsedExisting)
            {
                result.Failures.Add(linkType.Name + " — Revit вернул статус «" + status + "»");
                return false;
            }

            return applied;
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
                        Logger.Log("  ВНИМАНИЕ: файл связи «" + linkName + "» открыт в этой же сессии Revit — "
                                   + "конфигурация наборов будет проигнорирована", 2);
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
                    Logger.Log("  дамп: документ связи недоступен", 2);
                    return;
                }

                Logger.Log("  дамп наборов связи (файл: " + linkDoc.PathName
                           + ", совместная работа: " + linkDoc.IsWorkshared + "):", 2);

                foreach (Workset workset in new FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset))
                {
                    Logger.Log("    «" + workset.Name + "» открыт: " + workset.IsOpen
                               + ", редактируемый: " + workset.IsEditable + ", вид: " + workset.Kind, 2);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("  дамп не удался: " + ex.Message, 2);
            }
        }

        /// <summary>
        /// Мы выгружали связь ради повторной попытки — нельзя оставить её выгруженной,
        /// если попытка не удалась: пользователь просил закрыть наборы, а не убрать связь.
        /// </summary>
        private static void EnsureLoaded(RevitLinkType linkType, CloseResult result)
        {
            try
            {
                if (linkType.GetLinkedFileStatus() == LinkedFileStatus.Loaded) return;

                linkType.Reload();
                Logger.Log("  связь возвращена в загруженное состояние после неудачной попытки", 2);
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
            Dictionary<ElementId, string> names, CloseResult result)
        {
            foreach (var pair in desired)
            {
                if (!result.Reloaded.Any(id => id == pair.Key)) continue;

                HashSet<string> actual = ReadClosedNames(doc, pair.Key);
                bool applied = actual.SetEquals(pair.Value);

                string name = names.ContainsKey(pair.Key) ? names[pair.Key] : pair.Key.ToString();
                Logger.Log("  «" + name + "» — после восстановления пути закрыто "
                           + actual.Count + " из " + pair.Value.Count
                           + (applied ? " — держится" : " — ОТКАТИЛОСЬ"), 2);

                if (applied) continue;

                result.Failures.Add(name + " — наборы закрылись, но откатились при восстановлении "
                                    + "относительного типа пути");
                result.Changed--;
                result.Reloaded.RemoveAll(id => id == pair.Key);
            }
        }

        private static void RestorePathTypes(Document doc, Dictionary<ElementId, PathType> pathTypes,
            CloseResult result)
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

        private static Document GetLinkDocument(Document doc, ElementId linkTypeId)
        {
            RevitLinkInstance instance = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .FirstOrDefault(i => i.GetTypeId() == linkTypeId);

            return instance?.GetLinkDocument();
        }

        /// <summary>
        /// Наборы связи парами «id — имя». Идентификаторы живут только до следующей загрузки,
        /// поэтому читаются заново перед каждой попыткой.
        /// </summary>
        private static List<KeyValuePair<WorksetId, string>> ReadWorksets(Document doc, ElementId linkTypeId)
        {
            var worksets = new List<KeyValuePair<WorksetId, string>>();

            Document linkDoc = GetLinkDocument(doc, linkTypeId);
            if (linkDoc == null || !linkDoc.IsWorkshared) return worksets;

            foreach (Workset workset in new FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset))
            {
                worksets.Add(new KeyValuePair<WorksetId, string>(workset.Id, workset.Name));
            }

            return worksets;
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
    }
}
