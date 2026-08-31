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
    public sealed class RebarNoMarkCheck : ObservableObject, ICheck
    {
        public const string CheckId = "rebar-no-mark";
        public const string DisplayTitle = "Арматура без марки";
        public const string ResultTitle = "У арматуры заполнена марка конструкции";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.RebarNoMarkNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public RebarNoMarkCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.RebarNoMarkNumber,
            DisplayTitle,
            ResultTitle,
            RebarNoMarkChecker.Run);

        public CheckRunResult Run(Document doc) => RebarNoMarkChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.RebarNoMarkNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Как TNovUtilsST.RebarNoMark: вся арматура (OST_Rebar) с пустым общим параметром
    /// A_Марка конструкции (GUID). Модель и вид не меняются.
    /// </summary>
    public static class RebarNoMarkChecker
    {
        private static readonly Guid ConstructionMarkGuid = new Guid("5d369dfb-17a2-4ae2-a1a1-bdfc33ba7405");

        public static CheckRunResult Run(Document doc)
        {
            var rebars = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsNotElementType()
                .ToList();

            var names = new List<string>();
            var ids = new List<string>();
            string paramName = "";

            foreach (var rebar in rebars)
            {
                if (string.IsNullOrEmpty(paramName) && Param.ParamExistByGuid(ConstructionMarkGuid, rebar))
                    paramName = rebar.get_Parameter(ConstructionMarkGuid)?.Definition?.Name ?? "";

                string value = Param.GetStringParamValue(doc, ConstructionMarkGuid, rebar);
                if (!string.IsNullOrEmpty(value)) continue;

                names.Add(rebar.Name);
                ids.Add(ElementIds.ToStringValue(rebar.Id));
            }

            string header = string.IsNullOrEmpty(paramName)
                ? "Арматура без марки конструкции"
                : $"Арматура с пустым параметром '{paramName}'";

            var log = "";
            if (ids.Count > 0)
                log = $"\n{header}: {string.Join(", ", names)}\nId: {string.Join(", ", ids)}\n";

            return new CheckRunResult
            {
                Title = RebarNoMarkCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log
            };
        }
    }
}
