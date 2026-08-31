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
    public sealed class NoPartsCheck : ObservableObject, ICheck
    {
        public const string CheckId = "no-parts";
        public const string DisplayTitle = "Отсутствуют элементы категории Части";
        public const string ResultTitle = "Отсутствуют элементы категории Части";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.NoPartsNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public NoPartsCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.NoPartsNumber,
            DisplayTitle,
            ResultTitle,
            NoPartsChecker.Run);

        public CheckRunResult Run(Document doc) => NoPartsChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.NoPartsNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Наличие экземпляров категории «Части» (OST_Parts) — ошибка.
    /// </summary>
    public static class NoPartsChecker
    {
        public static CheckRunResult Run(Document doc)
        {
            var parts = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Parts)
                .WhereElementIsNotElementType()
                .ToList();

            var names = new List<string>();
            var ids = new List<string>();
            foreach (var e in parts)
            {
                names.Add(e.Name);
                ids.Add(ElementIds.ToStringValue(e.Id));
            }

            var log = "";
            if (ids.Count > 0)
                log = $"\nНайдены элементы категории Части: {string.Join(", ", names)}\nId: {string.Join(", ", ids)}\n";

            return new CheckRunResult
            {
                Title = NoPartsCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log
            };
        }
    }
}
