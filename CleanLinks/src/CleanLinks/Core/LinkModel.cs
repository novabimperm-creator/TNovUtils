using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CleanLinks.Core
{
    /// <summary>Что сделать со связью целиком.</summary>
    public enum LinkStateAction
    {
        None,
        Unload,
        Load,
        Reload
    }

    /// <summary>Переключатель с третьим состоянием «не трогать».</summary>
    public enum TriState
    {
        Unchanged,
        On,
        Off
    }

    public class LinkWorksetInfo
    {
        public WorksetId Id { get; set; }
        public string Name { get; set; }
        public bool IsOpen { get; set; }

        public bool Matches(WorksetCategory category)
        {
            return category != null && category.Matches(Name);
        }

        /// <summary>Похоже ли имя набора на «оси/уровни» — для автоотметки в списке.</summary>
        public bool LooksLikeGrids => Matches(WorksetCategories.Grids);
    }

    public class LinkInfo
    {
        public ElementId TypeId { get; set; }
        public string Name { get; set; }
        public LinkedFileStatus Status { get; set; }
        public bool IsNested { get; set; }
        public bool IsLoaded { get; set; }
        public PathType PathType { get; set; }

        /// <summary>Есть ли ссылка, по которой связь можно перезагрузить.</summary>
        public bool CanReload { get; set; }

        public List<ElementId> InstanceIds { get; } = new List<ElementId>();
        public List<LinkWorksetInfo> Worksets { get; } = new List<LinkWorksetInfo>();

        /// <summary>Почему колонка рабочих наборов недоступна. null — доступна.</summary>
        public string WorksetProblem { get; set; }

        /// <summary>Почему колонки графики в виде недоступны. null — доступны.</summary>
        public string ViewProblem { get; set; }

        public bool Halftone { get; set; }
        public bool HiddenInView { get; set; }

        public bool WorksetsAvailable => WorksetProblem == null && Worksets.Count > 0;
        public bool ViewGraphicsAvailable => ViewProblem == null && InstanceIds.Count > 0;

        public List<LinkWorksetInfo> GridWorksets =>
            Worksets.Where(w => w.LooksLikeGrids).ToList();

        /// <summary>Наборы, попадающие хотя бы в одну из перечисленных групп.</summary>
        public List<LinkWorksetInfo> WorksetsMatching(IEnumerable<WorksetCategory> categories)
        {
            List<WorksetCategory> list = categories?.ToList() ?? new List<WorksetCategory>();
            return Worksets.Where(w => list.Any(w.Matches)).ToList();
        }

        public string StatusText
        {
            get
            {
                if (IsNested) return "вложенная";
                switch (Status)
                {
                    case LinkedFileStatus.Loaded: return "загружена";
                    case LinkedFileStatus.Unloaded: return "выгружена";
                    case LinkedFileStatus.NotFound: return "файл не найден";
                    case LinkedFileStatus.LocallyUnloaded: return "выгружена локально";
                    default: return Status.ToString();
                }
            }
        }
    }

    /// <summary>
    /// Что пользователь наметил сделать с одной связью. Рабочие наборы хранятся
    /// как разница с текущим состоянием, а не как желаемый список: так видно,
    /// что именно менять, и не нужно трогать связь, если менять нечего.
    /// </summary>
    public class LinkPlan
    {
        public LinkPlan(LinkInfo link)
        {
            Link = link ?? throw new ArgumentNullException(nameof(link));
        }

        public LinkInfo Link { get; }
        public ElementId TypeId => Link.TypeId;

        public List<WorksetId> WorksetsToClose { get; } = new List<WorksetId>();
        public List<WorksetId> WorksetsToOpen { get; } = new List<WorksetId>();

        public LinkStateAction Action { get; set; } = LinkStateAction.None;
        public TriState Halftone { get; set; } = TriState.Unchanged;
        public TriState HideInView { get; set; } = TriState.Unchanged;

        public bool HasWorksetChanges => WorksetsToClose.Count > 0 || WorksetsToOpen.Count > 0;

        public bool HasViewChanges => Halftone != TriState.Unchanged || HideInView != TriState.Unchanged;

        public bool IsEmpty => !HasWorksetChanges && !HasViewChanges && Action == LinkStateAction.None;

        /// <summary>Будет ли набор закрыт после применения плана.</summary>
        public bool WillBeClosed(LinkWorksetInfo workset)
        {
            if (Contains(WorksetsToClose, workset.Id)) return true;
            if (Contains(WorksetsToOpen, workset.Id)) return false;
            return !workset.IsOpen;
        }

        /// <summary>Задаёт желаемое состояние набора; совпадение с текущим убирает его из плана.</summary>
        public void SetDesiredClosed(LinkWorksetInfo workset, bool closed)
        {
            Remove(WorksetsToClose, workset.Id);
            Remove(WorksetsToOpen, workset.Id);

            bool currentlyClosed = !workset.IsOpen;
            if (closed == currentlyClosed) return;

            (closed ? WorksetsToClose : WorksetsToOpen).Add(workset.Id);
        }

        /// <summary>Сводное состояние наборов с осями: On — все будут закрыты, Off — все открыты.</summary>
        public TriState GridState
        {
            get
            {
                List<LinkWorksetInfo> grids = Link.GridWorksets;
                if (grids.Count == 0) return TriState.Unchanged;

                bool allClosed = grids.All(WillBeClosed);
                if (allClosed) return TriState.On;

                bool allOpen = grids.All(w => !WillBeClosed(w));
                return allOpen ? TriState.Off : TriState.Unchanged;
            }
        }

        public void SetGridState(TriState state)
        {
            if (state == TriState.Unchanged) return;
            foreach (LinkWorksetInfo workset in Link.GridWorksets)
            {
                SetDesiredClosed(workset, state == TriState.On);
            }
        }

        /// <summary>Короткая сводка по наборам для ячейки таблицы.</summary>
        public string WorksetSummary()
        {
            if (!Link.WorksetsAvailable) return "недоступно";

            var parts = new List<string>();
            if (WorksetsToClose.Count > 0) parts.Add("закрыть " + WorksetsToClose.Count);
            if (WorksetsToOpen.Count > 0) parts.Add("открыть " + WorksetsToOpen.Count);

            if (parts.Count > 0) return string.Join(", ", parts);

            int closed = Link.Worksets.Count(w => !w.IsOpen);
            return closed > 0
                ? "наборов " + Link.Worksets.Count + ", закрыто " + closed
                : "наборов " + Link.Worksets.Count;
        }

        private static bool Contains(List<WorksetId> list, WorksetId id)
        {
            return list.Any(x => x.IntegerValue == id.IntegerValue);
        }

        private static void Remove(List<WorksetId> list, WorksetId id)
        {
            list.RemoveAll(x => x.IntegerValue == id.IntegerValue);
        }
    }

    /// <summary>Итог применения планов ко всем связям.</summary>
    public class ApplyResult
    {
        public int ViewChanged { get; set; }
        public int WorksetChanged { get; set; }
        public int StateChanged { get; set; }

        /// <summary>Что не получилось.</summary>
        public List<string> Failures { get; } = new List<string>();

        /// <summary>Что сделано не так, как просили, но без ошибки.</summary>
        public List<string> Notes { get; } = new List<string>();

        public int TotalChanged => ViewChanged + WorksetChanged + StateChanged;
    }
}
