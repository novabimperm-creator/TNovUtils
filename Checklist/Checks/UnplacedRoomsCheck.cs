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
    public sealed class UnplacedRoomsCheck : ObservableObject, ICheck
    {
        public const string CheckId = "unplaced-rooms";
        public const string DisplayTitle = "Неразмещенные помещения";
        public const string ResultTitle = "Нет неразмещенных и избыточных помещений";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.UnplacedRoomsNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public UnplacedRoomsCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.UnplacedRoomsNumber,
            DisplayTitle,
            ResultTitle,
            UnplacedRoomsChecker.Run);

        public CheckRunResult Run(Document doc) => UnplacedRoomsChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.UnplacedRoomsNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Как TNovRooms GateControl, этап 1: помещения с нулевой площадью (ROOM_AREA == 0).
    /// </summary>
    public static class UnplacedRoomsChecker
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
                if (!IsUnplaced(room)) continue;
                labels.Add(FormatRoom(room));
                ids.Add(ElementIds.ToStringValue(room.Id));
            }

            var log = "";
            if (ids.Count > 0)
                log = $"\nНеразмещенные или избыточные помещения (площадь 0): {string.Join(", ", labels)}\nId: {string.Join(", ", ids)}\n";

            return new CheckRunResult
            {
                Title = UnplacedRoomsCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log
            };
        }

        private static bool IsUnplaced(Room room)
        {
            Parameter p = room.get_Parameter(BuiltInParameter.ROOM_AREA);
            return p == null || p.AsDouble() == 0;
        }

        private static string FormatRoom(Room room)
        {
            string number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
            string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name ?? "";
            string level = room.Level == null ? "" : room.Level.Name;
            string label = string.IsNullOrWhiteSpace(number) ? name : number + " " + name;
            if (!string.IsNullOrEmpty(level))
                label += " (" + level + ")";
            return label.Trim();
        }
    }
}
