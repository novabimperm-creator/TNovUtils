using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class RoomDepartmentCheck : ObservableObject, ICheck
    {
        public const string CheckId = "room-department";
        public const string DisplayTitle = "Заполненность Назначения помещений";
        public const string ResultTitle = "У всех помещений заполнено Назначение";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.RoomDepartmentNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public RoomDepartmentCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.RoomDepartmentNumber,
            DisplayTitle,
            ResultTitle,
            RoomDepartmentChecker.Run);

        public CheckRunResult Run(Document doc) => RoomDepartmentChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.RoomDepartmentNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Как TNovRooms GateControl, этап 2: помещения без параметра «Назначение» (ROOM_DEPARTMENT).
    /// </summary>
    public static class RoomDepartmentChecker
    {
        public static CheckRunResult Run(Document doc)
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>();

            var labels = new List<string>();
            var ids = new List<string>();

            foreach (var room in rooms)
            {
                if (HasDepartment(room)) continue;
                labels.Add(FormatRoom(room));
                ids.Add(ElementIds.ToStringValue(room.Id));
            }

            var log = "";
            if (ids.Count > 0)
                log = $"\nПомещения без «Назначения»: {string.Join(", ", labels)}\nId: {string.Join(", ", ids)}\n";

            return new CheckRunResult
            {
                Title = RoomDepartmentCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log
            };
        }

        private static bool HasDepartment(Room room)
        {
            Parameter p = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
            if (p == null || !p.HasValue) return false;
            string value = p.AsString();
            return value != null && value.Trim().Length > 0;
        }

        private static string FormatRoom(Room room)
        {
            string number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
            string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name ?? "";
            string label = string.IsNullOrWhiteSpace(number) ? name : number + " " + name;
            return label.Trim();
        }
    }
}
