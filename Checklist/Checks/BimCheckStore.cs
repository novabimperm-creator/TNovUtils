using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using TNovCommon;
using TNovCommon.Storage;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// JSON BIM-проверок: {docName},BIM проверки.json рядом с autocheck/checklist (или документ bimcheck в TNovApi).
    /// </summary>
    public sealed class BimCheckStore : ObservableObject, IDisposable
    {
        private static readonly JsonSerializerSettings SaveSettings = new JsonSerializerSettings { Formatting = Formatting.Indented };

        private readonly ChecklistSession _session;
        private readonly SharedDocument<List<SavedState>> _doc;
        private readonly IReadOnlyList<string> _visibleIds;
        private bool _applying;
        private bool _busy;
        private bool _disposed;

        public ObservableCollection<BimCheckItem> Items { get; } = new ObservableCollection<BimCheckItem>();

        public event EventHandler Changed;

        public CheckStatus AggregateStatus => BimCheckItem.AggregateStatus(Items);
        public int PassedCount => Items.Count(i => i.IsChecked);
        public int TotalCount => Items.Count;
        public string CountText => $"{PassedCount}/{TotalCount}";

        /// <summary>Отметки и комментарии можно менять (сервер доступен, данные загружены).</summary>
        public bool CanEdit => _doc.CanEdit;

        public BimCheckStore(Document doc, ChecklistSession session)
        {
            _session = session;
            foreach (var item in BimCheckItem.Catalog())
            {
                if (!item.IsVisibleFor(doc)) continue;
                item.PropertyChanged += Item_PropertyChanged;
                Items.Add(item);
            }
            _visibleIds = Items.Select(i => i.Id).ToList();

            _doc = new SharedDocument<List<SavedState>>(
                session, DocumentKinds.BimCheck, "BIM-проверки", Dispatcher.CurrentDispatcher,
                Parse, Serialize, ApplyServer, () => _busy || _disposed);
            _doc.CanEditChanged += (s, e) => OnPropertyChanged(nameof(CanEdit));
            _doc.Start();
        }

        /// <summary>Внеочередная проверка сервера (без блокировки UI).</summary>
        public void CheckServerNow() => _session.CheckNow();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Dispose();
            foreach (var item in Items)
                item.PropertyChanged -= Item_PropertyChanged;
        }

        public void SetBusy(bool busy) => _busy = busy;

        private void Item_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_applying || _disposed) return;
            if (e.PropertyName != nameof(BimCheckItem.IsChecked) &&
                e.PropertyName != nameof(BimCheckItem.Comment))
                return;

            var item = (BimCheckItem)sender;
            _applying = true;
            try
            {
                item.LastChangedAt = DateTime.Now;
                item.LastChangedBy = RevitAPI.UiApplication?.Application?.Username ?? "";
            }
            finally
            {
                _applying = false;
            }

            // Операция над свежей копией: только этот пункт, чужие отметки по другим пунктам не трогаем.
            string id = item.Id;
            bool isChecked = item.IsChecked;
            DateTime at = item.LastChangedAt;
            string by = item.LastChangedBy;
            string comment = item.Comment ?? "";
            bool commentChanged = e.PropertyName == nameof(BimCheckItem.Comment);

            if (!_doc.Edit(states =>
                {
                    var state = states.FirstOrDefault(s => s.Id == id);
                    if (state == null)
                    {
                        state = new SavedState { Id = id, Comment = "" };
                        states.Add(state);
                    }
                    if (commentChanged) state.Comment = comment;
                    else state.IsChecked = isChecked;
                    state.CreatedAt = at;
                    state.Creator = by;
                    return true;
                }))
            {
                // Правка сейчас невозможна (элементы и так заблокированы) — вернуть как на сервере.
                _doc.Reload();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void ApplySaved(List<SavedState> saved)
        {
            _applying = true;
            try
            {
                foreach (var item in Items)
                {
                    var state = saved.FirstOrDefault(s => s.Id == item.Id);
                    if (state == null) continue;
                    item.IsChecked = state.IsChecked;
                    item.LastChangedAt = state.CreatedAt;
                    item.LastChangedBy = state.Creator;
                    item.Comment = state.Comment ?? "";
                }
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// Фон: разбор документа. Недостающие видимые пункты дописываются пустыми — как раньше
        /// Save() писал весь список окна, так что формат файла не меняется.
        /// </summary>
        private List<SavedState> Parse(string json)
        {
            var list = string.IsNullOrEmpty(json)
                ? new List<SavedState>()
                : JsonConvert.DeserializeObject<List<SavedState>>(json) ?? new List<SavedState>();
            foreach (string id in _visibleIds)
                if (!list.Any(s => s.Id == id))
                    list.Add(new SavedState { Id = id, Comment = "" });
            return list;
        }

        private static string Serialize(List<SavedState> states) => JsonConvert.SerializeObject(states, SaveSettings);

        /// <summary>UI-поток: документ с сервера (загрузка, опрос или итог своего сохранения).</summary>
        private void ApplyServer(StoredDocument stored, List<SavedState> server)
        {
            var snapshot = Items
                .Select(i => (i.Id, i.IsChecked, i.LastChangedAt, i.LastChangedBy, i.Comment))
                .ToList();
            if (!HasMeaningfulChange(snapshot, server)) return;

            ApplySaved(server);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static bool HasMeaningfulChange(
            List<(string Id, bool IsChecked, DateTime LastChangedAt, string LastChangedBy, string Comment)> local,
            List<SavedState> server)
        {
            if (server == null) return false;

            foreach (var item in local)
            {
                var remote = server.FirstOrDefault(s => s.Id == item.Id);
                if (remote == null)
                {
                    if (item.IsChecked || item.LastChangedAt.Year >= 2000 || !string.IsNullOrEmpty(item.Comment))
                        return true;
                    continue;
                }
                if (remote.IsChecked != item.IsChecked ||
                    remote.CreatedAt != item.LastChangedAt ||
                    remote.Creator != item.LastChangedBy ||
                    (remote.Comment ?? "") != (item.Comment ?? ""))
                    return true;
            }
            return false;
        }

        private sealed class SavedState
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("is_done")]
            public bool IsChecked { get; set; }

            [JsonProperty("created_at")]
            public DateTime CreatedAt { get; set; }

            [JsonProperty("creator")]
            public string Creator { get; set; }

            [JsonProperty("comment")]
            public string Comment { get; set; }
        }
    }
}
