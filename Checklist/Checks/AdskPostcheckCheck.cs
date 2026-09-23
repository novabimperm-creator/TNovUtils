using System;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class AdskPostcheckCheck : ObservableObject, ICheck
    {
        public const string CheckId = "adsk-postcheck";
        public const string DisplayTitle = Report.ChecklistCatalog.AdskPostcheckTitle;
        public const string ResultTitle = "ADSK_Количество и ADSK_Группирование заполнены";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.AdskPostcheckNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public AdskPostcheckCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.AdskPostcheckNumber,
            DisplayTitle,
            ResultTitle,
            AdskPostcheckChecker.Run);

        public CheckRunResult Run(Document doc) => AdskPostcheckChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.AdskPostcheckNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Та же постпроверка, что в конце сценария ВК ОВ в MEPSpec (TNovCommon.VkovPostcheck):
    /// ADSK_Количество не назначено / = 0 при длине > 500 мм, ADSK_Группирование не заполнено.
    /// Элементы с ADSK_Наименование «!Не учитывать» пропускаются. Модель не меняет.
    /// </summary>
    public static class AdskPostcheckChecker
    {
        public static CheckRunResult Run(Document doc)
        {
            var issues = VkovPostcheck.FindIssues(VkovPostcheck.Collect(doc));
            var ids = issues.Select(i => ElementIds.ToStringValue(i.ElementId)).ToList();

            var log = "";
            if (issues.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"Найдено проблемных элементов: {issues.Count}. Проверьте ADSK_Количество и ADSK_Группирование.");
                var groups = issues
                    .GroupBy(i => new
                    {
                        Grouping = i.Grouping ?? "",
                        Category = i.Category ?? "",
                        Problems = string.Join(", ", i.Problems)
                    })
                    .OrderBy(g => g.Key.Grouping, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Problems, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Category, StringComparer.Ordinal);
                foreach (var g in groups)
                {
                    string grouping = string.IsNullOrEmpty(g.Key.Grouping) ? "(без группирования)" : g.Key.Grouping;
                    sb.AppendLine();
                    sb.AppendLine($"{grouping} | {g.Key.Category} | {g.Key.Problems} — {g.Count()} шт.");
                    sb.AppendLine("Id: " + string.Join(", ", g.Select(i => ElementIds.ToStringValue(i.ElementId))));
                }
                log = sb.ToString();
            }

            return new CheckRunResult
            {
                Title = AdskPostcheckCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log
            };
        }
    }
}
