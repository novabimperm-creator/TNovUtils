using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TNovUtils.LinkWorksets
{
    /// <summary>Рабочий набор внутри связанного файла.</summary>
    public sealed class LinkWorkset
    {
        public string Name { get; set; }
        public bool IsOpen { get; set; }
    }

    /// <summary>Состояние одной RVT-связи с точки зрения её рабочих наборов.</summary>
    public sealed class LinkInfo
    {
        public ElementId TypeId { get; set; }
        public string Name { get; set; }

        /// <summary>Почему до наборов связи не дотянуться. null — доступны.</summary>
        public string Problem { get; set; }

        public List<LinkWorkset> Worksets { get; } = new List<LinkWorkset>();

        public bool Available => Problem == null && Worksets.Count > 0;

        /// <summary>Наборы связи, попадающие в группу по имени.</summary>
        public List<LinkWorkset> Matching(WorksetGroup group)
        {
            return Worksets.Where(w => group.Matches(w.Name)).ToList();
        }
    }

    /// <summary>
    /// Читает RVT-связи проекта и состояние их рабочих наборов.
    ///
    /// Наборы связи — единственный доступный рычаг: настройки отображения связей на уровне
    /// категорий Autodesk в API не открыла (в 2022 нет вовсе, с 2024 есть только
    /// LinkVisibilityType и LinkedViewId). Закрытый набор не грузится вовсе, поэтому оси
    /// исчезают сразу во всех видах — и в 3D, и в разрезах.
    /// </summary>
    public static class LinkScanner
    {
        public static List<LinkInfo> Collect(Document doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var instancesByType = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .GroupBy(i => i.GetTypeId())
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<LinkInfo>();

            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .OrderBy(t => t.Name, StringComparer.CurrentCulture)
                .ToList();

            foreach (RevitLinkType linkType in linkTypes)
            {
                var info = new LinkInfo { TypeId = linkType.Id, Name = linkType.Name };
                result.Add(info);

                List<RevitLinkInstance> instances;
                instancesByType.TryGetValue(linkType.Id, out instances);

                FillWorksets(info, linkType, instances);
            }

            return result;
        }

        private static void FillWorksets(LinkInfo info, RevitLinkType linkType, List<RevitLinkInstance> instances)
        {
            if (linkType.IsNestedLink)
            {
                info.Problem = "вложенная связь — управляется из родительского файла";
                return;
            }
            if (linkType.GetLinkedFileStatus() != LinkedFileStatus.Loaded)
            {
                info.Problem = "связь не загружена — наборы прочитать нельзя";
                return;
            }

            // IsFromLocalPath() здесь не годится: связи с Revit Server (RSN://) отдают false,
            // хотя LoadFrom(ModelPath, WorksetConfiguration) с серверным путём работает.
            if (!linkType.IsExternalFileReference() && !linkType.RefersToExternalResourceReferences())
            {
                info.Problem = "у связи нет внешней ссылки — перезагрузка не поддерживается";
                return;
            }

            if (instances == null || instances.Count == 0)
            {
                info.Problem = "у связи нет экземпляра в проекте";
                return;
            }

            Document linkDoc = instances[0].GetLinkDocument();
            if (linkDoc == null)
            {
                info.Problem = "документ связи недоступен";
                return;
            }
            if (!linkDoc.IsWorkshared)
            {
                info.Problem = "в связанном файле нет совместной работы — рабочих наборов нет";
                return;
            }

            foreach (Workset workset in new FilteredWorksetCollector(linkDoc).OfKind(WorksetKind.UserWorkset))
            {
                info.Worksets.Add(new LinkWorkset { Name = workset.Name, IsOpen = workset.IsOpen });
            }

            info.Worksets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

            if (info.Worksets.Count == 0)
            {
                info.Problem = "пользовательских рабочих наборов не найдено";
            }
        }
    }
}
