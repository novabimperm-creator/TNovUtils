using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using TNovCommon;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Общий JSON автопроверок с Журналом: {docName},autocheck.json.
    /// </summary>
    public sealed class AutoCheckStore : IDisposable
    {
        public const int GridsLevelsLinksNumber = 1;
        public const int AntiMirrorNumber = 2;
        public const int RebarNoMarkNumber = 3;
        public const int NoPartsNumber = 4;
        public const int LintelsNoMarkNumber = 5;
        public const int EvacuationRoutesNumber = 6;
        public const int UnplacedRoomsNumber = 7;
        public const int RoomDepartmentNumber = 8;

        private readonly string _jsonPath;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private bool _busy;
        private bool _disposed;
        private List<AutoCheckItem> _items = new List<AutoCheckItem>();

        public string LogsRootFolder { get; }

        public event EventHandler Changed;

        public AutoCheckStore(Document doc)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _jsonPath = JsonDataService.GetJsonPath(doc, "autocheck");
            LogsRootFolder = string.IsNullOrEmpty(_jsonPath)
                ? null
                : Path.Combine(Path.GetDirectoryName(_jsonPath),
                    Path.GetFileNameWithoutExtension(_jsonPath) + "_checklogs");

            if (!string.IsNullOrEmpty(LogsRootFolder))
                Directory.CreateDirectory(LogsRootFolder);

            Reload(DateTime.Now);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (s, e) => Poll();
            _timer.Start();
        }

        public AutoCheckItem Get(int number) =>
            _items.FirstOrDefault(i => i.Number == number);

        public void ApplyRun(int number, CheckRunResult result, string userName)
        {
            ApplyRunCore(number, result, userName);
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyRuns(IReadOnlyList<(int Number, CheckRunResult Result)> results, string userName)
        {
            foreach (var r in results)
                ApplyRunCore(r.Number, r.Result, userName);
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyRunCore(int number, CheckRunResult result, string userName)
        {
            var item = GetOrCreate(number);
            item.Title = result.Title;
            item.IsChecked = result.Passed;
            item.ElemIds = result.ElemIds ?? "";
            item.Creator = userName;
            item.CreationDate = DateTime.Now;
            item.SetLogsRootFolder(LogsRootFolder);

            if (!string.IsNullOrEmpty(item.LogFullPath))
                File.WriteAllText(item.LogFullPath + ".txt", result.Log ?? "");
        }

        public void SetBusy(bool busy) => _busy = busy;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }

        private AutoCheckItem GetOrCreate(int number)
        {
            var item = Get(number);
            if (item != null) return item;

            item = new AutoCheckItem { Number = number };
            item.SetLogsRootFolder(LogsRootFolder);
            _items.Add(item);
            return item;
        }

        private void Reload(DateTime now)
        {
            try
            {
                _items = string.IsNullOrEmpty(_jsonPath)
                    ? new List<AutoCheckItem>()
                    : JsonDataService.LoadAuto(_jsonPath, now);

                foreach (var item in _items)
                    item.SetLogsRootFolder(LogsRootFolder);
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось загрузить автопроверки: {ex.Message}").ShowDialog();
                _items = new List<AutoCheckItem>();
            }
        }

        private void Save()
        {
            try
            {
                if (!string.IsNullOrEmpty(_jsonPath))
                    JsonDataService.SaveAuto(_jsonPath, _items);
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось сохранить автопроверки: {ex.Message}").ShowDialog();
            }
        }

        private void Poll()
        {
            if (_busy || _disposed || string.IsNullOrEmpty(_jsonPath)) return;

            var snapshot = _items
                .Select(i => (i.Number, i.CreationDate, i.IsChecked, i.Title))
                .ToList();

            Task.Run(() =>
            {
                try
                {
                    var server = JsonDataService.LoadAuto(_jsonPath, DateTime.Now);
                    _dispatcher.Invoke(() =>
                    {
                        if (_busy || _disposed) return;
                        if (!HasMeaningfulChange(snapshot, server)) return;

                        _items = server;
                        foreach (var item in _items)
                            item.SetLogsRootFolder(LogsRootFolder);
                        Changed?.Invoke(this, EventArgs.Empty);
                    });
                }
                catch { /* опрос не должен ронять окно */ }
            });
        }

        private static bool HasMeaningfulChange(
            List<(int Number, DateTime CreationDate, bool IsChecked, string Title)> local,
            List<AutoCheckItem> server)
        {
            if (server == null) return false;
            if (local.Count != server.Count) return true;

            foreach (var remote in server)
            {
                var ours = local.FirstOrDefault(i => i.Number == remote.Number);
                if (ours.Number == 0 && remote.Number != 0)
                    return true;
                if (ours.CreationDate != remote.CreationDate ||
                    ours.IsChecked != remote.IsChecked ||
                    ours.Title != remote.Title)
                    return true;
            }
            return false;
        }
    }

    public sealed class CheckRunResult
    {
        public string Title { get; set; }
        public bool Passed { get; set; }
        public string ElemIds { get; set; }
        public string Log { get; set; }
    }
}
