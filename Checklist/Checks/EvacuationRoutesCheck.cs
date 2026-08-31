using System;
using System.Linq;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class EvacuationRoutesCheck : ObservableObject, ICheck
    {
        public const string CheckId = "evacuation-routes";
        public const string DisplayTitle = "Смоделированы пути эвакуации";
        public const string ResultTitle = "Смоделированы пути эвакуации";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.EvacuationRoutesNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public EvacuationRoutesCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.EvacuationRoutesNumber,
            DisplayTitle,
            ResultTitle,
            EvacuationRoutesChecker.Run);

        public CheckRunResult Run(Document doc) => EvacuationRoutesChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.EvacuationRoutesNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Хотя бы один экземпляр категории «Антураж» (OST_Entourage). Если нет — ошибка.
    /// </summary>
    public static class EvacuationRoutesChecker
    {
        public static CheckRunResult Run(Document doc)
        {
            int count = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Entourage)
                .WhereElementIsNotElementType()
                .GetElementCount();

            bool passed = count > 0;
            return new CheckRunResult
            {
                Title = EvacuationRoutesCheck.ResultTitle,
                Passed = passed,
                ElemIds = "",
                Log = passed
                    ? $"\nЭлементов категории Антураж: {count}\n"
                    : "\nЭлементы категории Антураж не найдены.\n"
            };
        }
    }
}
