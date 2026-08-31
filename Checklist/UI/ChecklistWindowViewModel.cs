using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TNovCommon;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public sealed class NavItem : ObservableObject
    {
        public string Id { get; }
        public string Title { get; }
        public bool IsSummary { get; }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        private Brush _statusBrush;
        public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

        public Visibility StatusVisibility => IsSummary ? Visibility.Collapsed : Visibility.Visible;

        public NavItem(string id, string title, bool isSummary)
        {
            Id = id;
            Title = title;
            IsSummary = isSummary;
        }

        public void ApplyStatus(CheckStatus status)
        {
            StatusBrush = CheckStatusBrushes.Of(status);
        }

        public void ApplyStatus(ICheck check)
        {
            if (check == null) return;
            ApplyStatus(check.Status);
        }
    }

    public sealed class ChecklistWindowViewModel : ObservableObject
    {
        private readonly Func<string, UserControl> _createView;
        private readonly Dictionary<string, UserControl> _cache = new Dictionary<string, UserControl>();

        public ObservableCollection<NavItem> NavItems { get; } = new ObservableCollection<NavItem>();
        public RelayCommand2 SelectNavCommand { get; }

        private UserControl _currentContent;
        public UserControl CurrentContent
        {
            get => _currentContent;
            private set => SetProperty(ref _currentContent, value);
        }

        public ChecklistWindowViewModel(CheckRegistry registry, BimCheckStore bimStore, Func<string, UserControl> createView)
        {
            _createView = createView;

            NavItems.Add(new NavItem(CheckRegistry.SummaryId, CheckRegistry.SummaryTitle, isSummary: true));

            var bimNav = new NavItem(CheckRegistry.BimChecksId, CheckRegistry.BimChecksTitle, isSummary: false);
            bimNav.ApplyStatus(bimStore.AggregateStatus);
            NavItems.Add(bimNav);
            bimStore.Changed += (s, e) => bimNav.ApplyStatus(bimStore.AggregateStatus);

            foreach (var check in registry.Checks)
            {
                var nav = new NavItem(check.Id, check.Title, isSummary: false);
                nav.ApplyStatus(check);
                NavItems.Add(nav);
                check.PropertyChanged += Check_PropertyChanged;
            }

            SelectNavCommand = new RelayCommand2(p => Select(p as string));
        }

        public void Select(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!_cache.TryGetValue(id, out var view))
            {
                view = _createView(id);
                _cache[id] = view;
            }

            CurrentContent = view;
            foreach (var nav in NavItems)
                nav.IsSelected = nav.Id == id;
        }

        public void DisposeViews()
        {
            foreach (var view in _cache.Values)
            {
                if (view is SummaryControl summary)
                    summary.StopPolling();
            }
        }

        private void Check_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!(sender is ICheck check)) return;
            var nav = FindNav(check.Id);
            nav?.ApplyStatus(check);
        }

        private NavItem FindNav(string id)
        {
            foreach (var nav in NavItems)
                if (nav.Id == id) return nav;
            return null;
        }
    }
}
