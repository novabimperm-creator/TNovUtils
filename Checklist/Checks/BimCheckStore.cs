using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using TNovCommon;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// JSON BIM-проверок: {docName},BIM проверки.json рядом с autocheck/checklist.
    /// </summary>
    public sealed class BimCheckStore : IDisposable
    {
        private readonly string _jsonPath;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private bool _applying;
        private bool _busy;
        private bool _disposed;

        public ObservableCollection<BimCheckItem> Items { get; } = new ObservableCollection<BimCheckItem>();

        public event EventHandler Changed;

        public CheckStatus AggregateStatus => BimCheckItem.AggregateStatus(Items);
        public int PassedCount => Items.Count(i => i.IsChecked);
        public int TotalCount => Items.Count;
        public string CountText => $"{PassedCount}/{TotalCount}";

        public BimCheckStore(Document doc)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _jsonPath = JsonDataService.GetJsonPath(doc, "BIM проверки");

            foreach (var item in BimCheckItem.Catalog())
            {
                if (!item.IsVisibleFor(doc)) continue;
                item.PropertyChanged += Item_PropertyChanged;
                Items.Add(item);
            }

            ApplySaved(LoadSaved());

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (s, e) => Poll();
            _timer.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
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

            Save();
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

        private List<SavedState> LoadSaved()
        {
            if (string.IsNullOrEmpty(_jsonPath) || !File.Exists(_jsonPath))
                return new List<SavedState>();

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string json = File.ReadAllText(_jsonPath);
                    return JsonConvert.DeserializeObject<List<SavedState>>(json) ?? new List<SavedState>();
                }
                    catch (IOException)
                    {
                        Thread.Sleep(300);
                    }
                    catch
                    {
                        return new List<SavedState>();
                    }
            }

            return new List<SavedState>();
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_jsonPath)) return;

            var payload = Items.Select(i => new SavedState
            {
                Id = i.Id,
                IsChecked = i.IsChecked,
                CreatedAt = i.LastChangedAt,
                Creator = i.LastChangedBy,
                Comment = i.Comment ?? ""
            }).ToList();

            var settings = new JsonSerializerSettings { Formatting = Formatting.Indented };
            string json = JsonConvert.SerializeObject(payload, settings);

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.WriteAllText(_jsonPath, json);
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(300);
                    }
                }
                throw new IOException($"Не удалось сохранить файл {_jsonPath} после трёх попыток.");
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось сохранить BIM-проверки: {ex.Message}").ShowDialog();
            }
        }

        private void Poll()
        {
            if (_busy || _disposed || string.IsNullOrEmpty(_jsonPath)) return;

            var snapshot = Items
                .Select(i => (i.Id, i.IsChecked, i.LastChangedAt, i.LastChangedBy, i.Comment))
                .ToList();

            Task.Run(() =>
            {
                try
                {
                    var server = LoadSaved();
                    _dispatcher.Invoke(() =>
                    {
                        if (_busy || _disposed) return;
                        if (!HasMeaningfulChange(snapshot, server)) return;
                        ApplySaved(server);
                        Changed?.Invoke(this, EventArgs.Empty);
                    });
                }
                catch { /* опрос не должен ронять окно */ }
            });
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
