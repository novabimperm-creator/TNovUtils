using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class DwgCurrentViewOnlyCheck : ObservableObject, ICheck
    {
        public const string CheckId = "dwg-current-view-only";
        public const string DisplayTitle = Report.ChecklistCatalog.DwgCurrentViewOnlyTitle;
        public const string ResultTitle = "Все связи и импорты DWG вставлены с опцией Только текущий вид";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.DwgCurrentViewOnlyNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public DwgCurrentViewOnlyCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.DwgCurrentViewOnlyNumber,
            DisplayTitle,
            ResultTitle,
            DwgCurrentViewOnlyChecker.Run);

        public CheckRunResult Run(Document doc) => DwgCurrentViewOnlyChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.DwgCurrentViewOnlyNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Связи и импорты CAD (ImportInstance) без опции «Только текущий вид» — ошибка.
    /// Такой экземпляр не принадлежит виду (ViewSpecific = false) и привязан к уровню, поэтому виден во всех видах.
    /// </summary>
    public static class DwgCurrentViewOnlyChecker
    {
        public static CheckRunResult Run(Document doc)
        {
            var bad = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .Where(i => !i.ViewSpecific)
                .Select(i => new { Id = ElementIds.ToStringValue(i.Id), Label = Label(doc, i) })
                .OrderBy(x => x.Label, StringComparer.Ordinal)
                .ToList();

            var log = new StringBuilder();
            if (bad.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Связи и импорты DWG без опции «Только текущий вид»: {bad.Count}");
                foreach (var x in bad)
                    log.AppendLine($"  {x.Id} {x.Label}");
                log.AppendLine("Id: " + string.Join(", ", bad.Select(x => x.Id)));
            }

            return new CheckRunResult
            {
                Title = DwgCurrentViewOnlyCheck.ResultTitle,
                Passed = bad.Count == 0,
                ElemIds = bad.Count > 0 ? string.Join(", ", bad.Select(x => x.Id)) : "",
                Log = log.ToString()
            };
        }

        private static string Label(Document doc, ImportInstance inst)
        {
            string name = inst.Category?.Name ?? inst.Name ?? "";
            string kind = inst.IsLinked ? "связь" : "импорт";
            string level = doc.GetElement(inst.LevelId) is Level l ? $", уровень «{l.Name}»" : "";
            return $"{name} ({kind}{level})";
        }
    }
}
