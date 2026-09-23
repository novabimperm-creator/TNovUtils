using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Report;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Пункт BIM-проверки. ModelNameMarkers — сочетания в имени модели,
    /// при которых пункт виден. Пустой список = все модели.
    /// </summary>
    public sealed class BimCheckItem : ObservableObject
    {
        public const int StaleCalendarDays = ChecklistCatalog.BimStaleCalendarDays;

        public string Id { get; set; }
        public string Title { get; set; }

        /// <summary>
        /// Сочетания в имени/заголовке модели. Пустой список — пункт для всех моделей.
        /// </summary>
        public List<string> ModelNameMarkers { get; set; } = new List<string>();

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }

        private DateTime _lastChangedAt;
        public DateTime LastChangedAt
        {
            get => _lastChangedAt;
            set
            {
                _lastChangedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOutdated));
                OnPropertyChanged(nameof(DisplayDate));
            }
        }

        private string _lastChangedBy;
        public string LastChangedBy
        {
            get => _lastChangedBy;
            set => SetProperty(ref _lastChangedBy, value);
        }

        private string _comment = "";
        public string Comment
        {
            get => _comment;
            set => SetProperty(ref _comment, value ?? "");
        }

        public string DisplayDate =>
            LastChangedAt.Year < 2000 ? "—" : LastChangedAt.ToString("dd.MM HH:mm");

        public bool IsOutdated
        {
            get
            {
                if (LastChangedAt.Year < 2000) return true;
                return (DateTime.Today - LastChangedAt.Date).TotalDays >= StaleCalendarDays;
            }
        }

        public bool IsVisibleFor(Document doc)
        {
            if (ModelNameMarkers == null || ModelNameMarkers.Count == 0)
                return true;
            return ModelNameRules.ContainsAny(doc, ModelNameMarkers.ToArray());
        }

        public bool IsVisibleFor(string modelName)
        {
            if (ModelNameMarkers == null || ModelNameMarkers.Count == 0)
                return true;
            return ModelNameRules.ContainsAny(modelName, ModelNameMarkers.ToArray());
        }

        /// <summary>
        /// Пункты берутся из общего каталога (Report\ChecklistCatalog.cs) — его же читает
        /// отчёт в Revit и в TNovDesktop, поэтому список правится только там.
        /// </summary>
        public static IReadOnlyList<BimCheckItem> Catalog()
        {
            return ChecklistCatalog.BimChecks
                .Select(d => new BimCheckItem
                {
                    Id = d.Id,
                    Title = d.Title,
                    ModelNameMarkers = d.Markers.ToList()
                })
                .ToList();
        }

        public static CheckStatus AggregateStatus(IReadOnlyList<BimCheckItem> items)
        {
            if (items == null || items.Count == 0)
                return CheckStatus.Outdated;

            int total = items.Count;
            int passed = 0;
            int outdated = 0;
            foreach (var item in items)
            {
                if (item.IsChecked) passed++;
                if (item.IsOutdated) outdated++;
            }

            if (passed == total && outdated == 0)
                return CheckStatus.Passed;

            // Красный: есть непройденные либо устарело строго больше половины.
            if (passed < total || outdated * 2 > total)
                return CheckStatus.Failed;

            return CheckStatus.Outdated;
        }
    }
}
