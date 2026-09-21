using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CleanLinks.Core;
using TNovCommon;

namespace CleanLinks.UI
{
    internal sealed class LinkManagerViewModel : ObservableObject
    {
        public ObservableCollection<LinkRowVm> Rows { get; }

        public IList<LinkPlan> Plans { get; }

        private string _summary;
        public string Summary
        {
            get => _summary;
            private set => SetProperty(ref _summary, value);
        }

        public LinkManagerViewModel(List<LinkInfo> links)
        {
            Plans = links.Select(l => new LinkPlan(l)).ToList();
            Rows = new ObservableCollection<LinkRowVm>(Plans.Select(p => new LinkRowVm(p, UpdateSummary)));
            UpdateSummary();
        }

        public void Reset()
        {
            foreach (LinkRowVm row in Rows)
                row.Reset();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            List<LinkPlan> active = Plans.Where(p => !p.IsEmpty).ToList();
            if (active.Count == 0)
            {
                Summary = "Ничего не выбрано.";
                return;
            }

            var parts = new List<string>();
            int worksets = active.Count(p => p.HasWorksetChanges);
            if (worksets > 0) parts.Add("наборы у " + worksets + " св.");
            int actions = active.Count(p => p.Action != LinkStateAction.None);
            if (actions > 0) parts.Add("состояние у " + actions + " св.");
            int viewChanges = active.Count(p => p.HasViewChanges);
            if (viewChanges > 0) parts.Add("графика вида у " + viewChanges + " св.");

            bool reloads = active.Any(p => p.HasWorksetChanges
                                           || p.Action == LinkStateAction.Reload
                                           || p.Action == LinkStateAction.Load);

            Summary = "Будет изменено: " + string.Join(", ", parts)
                      + (reloads ? ". Связи будут перезагружены." : ".");
        }
    }

    internal sealed class LinkRowVm : ObservableObject
    {
        public const string Keep = "—";
        public const string GridsHide = "Скрыть";
        public const string GridsShow = "Показать";
        public const string ActionUnload = "Выгрузить";
        public const string ActionLoad = "Загрузить";
        public const string ActionReload = "Перезагрузить";
        public const string HalftoneOn = "Включить";
        public const string HalftoneOff = "Выключить";
        public const string HideOn = "Скрыть";
        public const string HideOff = "Показать";

        private readonly System.Action _changed;
        private string _gridValue;
        private string _actionValue;
        private string _halftoneValue;
        private string _hideValue;
        private string _worksetSummary;

        public LinkRowVm(LinkPlan plan, System.Action changed)
        {
            Plan = plan;
            _changed = changed;
            LinkInfo link = plan.Link;

            Name = link.Name;
            Status = link.StatusText;

            GridEnabled = link.WorksetsAvailable && link.GridWorksets.Count > 0;
            GridOptions = GridEnabled
                ? new List<string> { Keep, GridsHide, GridsShow }
                : new List<string> { Keep };
            GridToolTip = !link.WorksetsAvailable
                ? link.WorksetProblem
                : link.GridWorksets.Count == 0
                    ? "В связи нет набора, похожего на оси или уровни — откройте «Наборы»."
                    : "Наборы: " + string.Join(", ", link.GridWorksets.Select(w => w.Name));
            _gridValue = GridEnabled ? FromGridState(plan.GridState) : Keep;

            WorksetsEnabled = link.WorksetsAvailable;
            WorksetsToolTip = link.WorksetsAvailable
                ? "Открыть полный список рабочих наборов связи"
                : link.WorksetProblem;
            _worksetSummary = plan.WorksetSummary();

            ActionEnabled = !link.IsNested;
            ActionToolTip = link.IsNested ? "Вложенная связь — управляется из родительского файла" : null;
            ActionOptions = new List<string> { Keep };
            if (ActionEnabled)
            {
                if (link.IsLoaded)
                {
                    ActionOptions.Add(ActionUnload);
                    if (link.CanReload) ActionOptions.Add(ActionReload);
                }
                else
                {
                    ActionOptions.Add(ActionLoad);
                }
            }
            _actionValue = Keep;

            ViewEnabled = link.ViewGraphicsAvailable;
            ViewOptions = ViewEnabled ? new List<string> { Keep, HalftoneOn, HalftoneOff } : new List<string> { Keep };
            HideOptions = ViewEnabled ? new List<string> { Keep, HideOn, HideOff } : new List<string> { Keep };
            HalftoneToolTip = ViewEnabled
                ? (link.Halftone ? "Сейчас: полутон включён" : "Сейчас: полутон выключен")
                : link.ViewProblem;
            HideToolTip = ViewEnabled
                ? (link.HiddenInView ? "Сейчас: скрыта в виде" : "Сейчас: видима")
                : link.ViewProblem;
            _halftoneValue = Keep;
            _hideValue = Keep;
        }

        public LinkPlan Plan { get; }
        public string Name { get; }
        public string Status { get; }

        public List<string> GridOptions { get; }
        public bool GridEnabled { get; }
        public string GridToolTip { get; }

        public string GridValue
        {
            get => _gridValue;
            set
            {
                if (!SetProperty(ref _gridValue, value) || !GridEnabled) return;
                Plan.SetGridState(ToGridState(value));
                RefreshWorksets();
                _changed();
            }
        }

        public bool WorksetsEnabled { get; }
        public string WorksetsToolTip { get; }

        public string WorksetSummary
        {
            get => _worksetSummary;
            private set => SetProperty(ref _worksetSummary, value);
        }

        public List<string> ActionOptions { get; }
        public bool ActionEnabled { get; }
        public string ActionToolTip { get; }

        public string ActionValue
        {
            get => _actionValue;
            set
            {
                if (!SetProperty(ref _actionValue, value) || !ActionEnabled) return;
                Plan.Action = ToAction(value);
                _changed();
            }
        }

        public List<string> ViewOptions { get; }
        public List<string> HideOptions { get; }
        public bool ViewEnabled { get; }
        public string HalftoneToolTip { get; }
        public string HideToolTip { get; }

        public string HalftoneValue
        {
            get => _halftoneValue;
            set
            {
                if (!SetProperty(ref _halftoneValue, value) || !ViewEnabled) return;
                Plan.Halftone = ToTriState(value, HalftoneOn, HalftoneOff);
                _changed();
            }
        }

        public string HideValue
        {
            get => _hideValue;
            set
            {
                if (!SetProperty(ref _hideValue, value) || !ViewEnabled) return;
                Plan.HideInView = ToTriState(value, HideOn, HideOff);
                _changed();
            }
        }

        public void RefreshWorksets()
        {
            WorksetSummary = Plan.WorksetSummary();
            if (GridEnabled)
            {
                string next = FromGridState(Plan.GridState);
                if (_gridValue != next)
                {
                    _gridValue = next;
                    OnPropertyChanged(nameof(GridValue));
                }
            }
            _changed();
        }

        public void Reset()
        {
            Plan.WorksetsToClose.Clear();
            Plan.WorksetsToOpen.Clear();
            Plan.Action = LinkStateAction.None;
            Plan.Halftone = TriState.Unchanged;
            Plan.HideInView = TriState.Unchanged;

            if (GridEnabled) _gridValue = Keep;
            _actionValue = Keep;
            _halftoneValue = Keep;
            _hideValue = Keep;
            WorksetSummary = Plan.WorksetSummary();
            OnPropertyChanged(nameof(GridValue));
            OnPropertyChanged(nameof(ActionValue));
            OnPropertyChanged(nameof(HalftoneValue));
            OnPropertyChanged(nameof(HideValue));
        }

        private static TriState ToGridState(string value)
        {
            if (value == GridsHide) return TriState.On;
            if (value == GridsShow) return TriState.Off;
            return TriState.Unchanged;
        }

        private static string FromGridState(TriState state)
        {
            switch (state)
            {
                case TriState.On: return GridsHide;
                case TriState.Off: return GridsShow;
                default: return Keep;
            }
        }

        private static TriState ToTriState(string value, string on, string off)
        {
            if (value == on) return TriState.On;
            if (value == off) return TriState.Off;
            return TriState.Unchanged;
        }

        private static LinkStateAction ToAction(string value)
        {
            switch (value)
            {
                case ActionUnload: return LinkStateAction.Unload;
                case ActionLoad: return LinkStateAction.Load;
                case ActionReload: return LinkStateAction.Reload;
                default: return LinkStateAction.None;
            }
        }
    }
}
