using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class GridsLevelsLinksCheck : ObservableObject, ICheck
    {
        public const string CheckId = "grids-levels-links";
        public const string DisplayTitle = "Оси, уровни, связи";
        public const string ResultTitle = "Оси, уровни и связи закреплены, помещены в свои наборы";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.GridsLevelsLinksNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public GridsLevelsLinksCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.GridsLevelsLinksNumber,
            DisplayTitle,
            ResultTitle,
            GridsLevelsLinksChecker.Run);

        public CheckRunResult Run(Document doc) => GridsLevelsLinksChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.GridsLevelsLinksNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Оси/уровни/связи: закрепление и рабочие наборы. Критерии как в Journal, без switch по номерам.
    /// </summary>
    public static class GridsLevelsLinksChecker
    {
        public static CheckRunResult Run(Document doc)
        {
            var grids = Collect<Autodesk.Revit.DB.Grid>(doc, BuiltInCategory.OST_Grids);
            var levels = Collect<Level>(doc, BuiltInCategory.OST_Levels);
            var links = Collect<RevitLinkInstance>(doc, BuiltInCategory.OST_RvtLinks);

            var log = new List<string>();
            var failIds = new List<string>();

            CollectUnpinned(grids, "Оси не закреплены", log, failIds);
            CollectUnpinned(levels, "Уровни не закреплены", log, failIds);
            CollectUnpinned(links, "Связи не закреплены", log, failIds);

            if (doc.IsWorkshared)
            {
                CollectWrongWorkset(grids, IsGridWorkset, "Оси не в своем наборе", log, failIds);
                CollectWrongWorkset(levels, IsLevelWorkset, "Уровни не в своем наборе", log, failIds);
                CollectWrongLinkWorkset(doc, links, log, failIds);
            }

            var ids = failIds.Distinct().ToList();
            return new CheckRunResult
            {
                Title = GridsLevelsLinksCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = string.Join("", log)
            };
        }

        private static List<T> Collect<T>(Document doc, BuiltInCategory category) where T : Element
        {
            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .Cast<T>()
                .ToList();
        }

        private static void CollectUnpinned<T>(IList<T> elements, string header, List<string> log, List<string> ids)
            where T : Element
        {
            var names = new List<string>();
            var localIds = new List<string>();
            foreach (var e in elements)
            {
                if (e.Pinned) continue;
                names.Add(e.Name);
                localIds.Add(ElementIds.ToStringValue(e.Id));
            }
            AppendFinding(header, names, localIds, log, ids);
        }

        private static void CollectWrongWorkset<T>(
            IList<T> elements, Func<string, bool> isValid, string header, List<string> log, List<string> ids)
            where T : Element
        {
            var names = new List<string>();
            var localIds = new List<string>();
            foreach (var e in elements)
            {
                if (isValid(WorksetName(e))) continue;
                names.Add(e.Name);
                localIds.Add(ElementIds.ToStringValue(e.Id));
            }
            AppendFinding(header, names, localIds, log, ids);
        }

        private static void CollectWrongLinkWorkset(
            Document doc, IList<RevitLinkInstance> links, List<string> log, List<string> ids)
        {
            var names = new List<string>();
            var localIds = new List<string>();
            foreach (var link in links)
            {
                string cleaned = CleanLinkName(link.Name);
                bool instanceOk = LinkWorksetOk(WorksetName(link), link.Name, cleaned);
                var linkType = doc.GetElement(link.GetTypeId());
                bool typeOk = linkType != null && LinkWorksetOk(WorksetName(linkType), link.Name, cleaned);
                if (instanceOk && typeOk) continue;

                names.Add(cleaned);
                localIds.Add(ElementIds.ToStringValue(link.Id));
            }
            AppendFinding("Связи не в своем наборе", names, localIds, log, ids);
        }

        private static bool LinkWorksetOk(string workset, string fullName, string cleanedName)
        {
            if (fullName.Contains("-РФ") || fullName.Contains("_РФ"))
                return IsLevelWorkset(workset);
            if (ContainsAny(fullName, "Задани", "задани", "-ЗД", "_ЗД", "ЗАДАНИЕ"))
                return workset.Contains("Задания смежникам");
            return workset.Contains(cleanedName);
        }

        private static string CleanLinkName(string name)
        {
            string head = (name ?? "").Split(':')[0];
            return head.Replace(".rvt", "");
        }

        private static bool IsGridWorkset(string name) => ContainsAny(name, "сетки", "Оси", "оси");
        private static bool IsLevelWorkset(string name) => ContainsAny(name, "слои", "Уровни", "уровни");

        private static bool ContainsAny(string value, params string[] parts)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var p in parts)
                if (value.Contains(p)) return true;
            return false;
        }

        private static string WorksetName(Element e) =>
            e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.AsValueString() ?? "";

        private static void AppendFinding(
            string header, List<string> names, List<string> localIds, List<string> log, List<string> allIds)
        {
            if (names.Count == 0) return;
            log.Add($"\n{header}: {string.Join(", ", names)}\nId: {string.Join(", ", localIds)}\n");
            allIds.AddRange(localIds);
        }
    }
}
