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
    public sealed class LintelsNoMarkCheck : ObservableObject, ICheck
    {
        public const string CheckId = "lintels-no-mark";
        public const string DisplayTitle = "Перемычки без марки";
        public const string ResultTitle = "У перемычек заполнена марка изделия";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.LintelsNoMarkNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public LintelsNoMarkCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.LintelsNoMarkNumber,
            DisplayTitle,
            ResultTitle,
            LintelsNoMarkChecker.Run);

        public CheckRunResult Run(Document doc) => LintelsNoMarkChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.LintelsNoMarkNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// FamilyInstance категории OST_StructuralFraming с пустым A_Марка изделия.
    /// </summary>
    public static class LintelsNoMarkChecker
    {
        private static readonly Guid ProductMarkGuid = new Guid("92ae0425-031b-40a9-8904-023f7389963b");

        public static CheckRunResult Run(Document doc)
        {
            var framing = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            var names = new List<string>();
            var ids = new List<string>();
            string paramName = "";

            foreach (var fi in framing)
            {
                if (string.IsNullOrEmpty(paramName) && Param.ParamExistByGuid(ProductMarkGuid, fi))
                    paramName = fi.get_Parameter(ProductMarkGuid)?.Definition?.Name ?? "";

                string value = Param.GetStringParamValue(doc, ProductMarkGuid, fi);
                if (!string.IsNullOrEmpty(value)) continue;

                names.Add(fi.Name);
                ids.Add(ElementIds.ToStringValue(fi.Id));
            }

            string header = string.IsNullOrEmpty(paramName)
                ? "Перемычки без марки изделия"
                : $"Перемычки с пустым параметром '{paramName}'";

            var log = "";
            if (ids.Count > 0)
                log = $"\n{header}: {string.Join(", ", names)}\nId: {string.Join(", ", ids)}\n";

            return new CheckRunResult
            {
                Title = LintelsNoMarkCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log
            };
        }
    }
}
