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
    public sealed class AntiMirrorCheck : ObservableObject, ICheck
    {
        public const string CheckId = "anti-mirror";
        public const string DisplayTitle = "Антизеркало";
        public const string ResultTitle = "Окна и двери не отзеркалены";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.AntiMirrorNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public AntiMirrorCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.AntiMirrorNumber,
            DisplayTitle,
            ResultTitle,
            AntiMirrorChecker.Run);

        public CheckRunResult Run(Document doc) => AntiMirrorChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.AntiMirrorNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Те же критерии отбора, что у TNovUtilsAR.Mirror: окна (группа «Окно» без точки) и двери
    /// (группа «Дверь»), плюс витражи по имени. Проверка только читает Mirrored, модель не меняет.
    /// </summary>
    public static class AntiMirrorChecker
    {
        public static CheckRunResult Run(Document doc)
        {
            var candidates = CollectCandidates(doc);
            var names = new List<string>();
            var ids = new List<string>();

            foreach (var fi in candidates)
            {
                if (!fi.Mirrored) continue;
                names.Add(fi.Name);
                ids.Add(ElementIds.ToStringValue(fi.Id));
            }

            var log = "";
            if (names.Count > 0)
                log = $"\nОтзеркалены: {string.Join(", ", names)}\nId: {string.Join(", ", ids)}\n";

            return new CheckRunResult
            {
                Title = AntiMirrorCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log
            };
        }

        private static List<FamilyInstance> CollectCandidates(Document doc)
        {
            var windows = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>();

            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>();

            return windows.Where(IsWindowCandidate)
                .Concat(doors.Where(IsDoorCandidate))
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .ToList();
        }

        private static bool IsWindowCandidate(FamilyInstance f)
        {
            if (NameContainsVitrage(f)) return true;
            string group = ModelGroup(f);
            return group != null && !group.Contains(".") && group.Contains("Окно");
        }

        private static bool IsDoorCandidate(FamilyInstance f)
        {
            if (NameContainsVitrage(f)) return true;
            return ModelGroup(f) == "Дверь";
        }

        private static bool NameContainsVitrage(FamilyInstance f) =>
            (f.Name ?? "").Contains("Витраж");

        private static string ModelGroup(FamilyInstance f) =>
            f.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)?.AsString();
    }
}
