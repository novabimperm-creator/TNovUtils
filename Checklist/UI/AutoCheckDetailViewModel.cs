using System;
using System.Windows.Media;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Checks;
using TNovUtils.Checklist.Revit;

namespace TNovUtils.Checklist.UI
{
    public sealed class AutoCheckDetailViewModel : ObservableObject
    {
        private readonly AutoCheckStore _store;
        private readonly int _number;
        private readonly string _defaultResultTitle;
        private readonly Func<Document, CheckRunResult> _run;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        public string HeaderTitle { get; }

        public RelayCommand2 RunCommand { get; }
        public RelayCommand2 OpenLogCommand { get; }
        public RelayCommand2 SelectElemsCommand { get; }

        private string _resultTitle;
        public string ResultTitle { get => _resultTitle; private set => SetProperty(ref _resultTitle, value); }

        private string _statusText = CheckStatusRules.Text(CheckStatus.Outdated);
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

        private Brush _statusBrush = CheckStatusBrushes.Outdated;
        public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

        private string _displayDate = "—";
        public string DisplayDate { get => _displayDate; private set => SetProperty(ref _displayDate, value); }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        private string _elemIds;
        public string ElemIds { get => _elemIds; private set => SetProperty(ref _elemIds, value); }

        public AutoCheckDetailViewModel(
            AutoCheckStore store,
            int number,
            string headerTitle,
            string defaultResultTitle,
            Func<Document, CheckRunResult> run)
        {
            _store = store;
            _number = number;
            HeaderTitle = headerTitle;
            _defaultResultTitle = defaultResultTitle;
            _resultTitle = defaultResultTitle;
            _run = run;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _store.Changed += (s, e) => Reload();
            Reload();

            RunCommand = new RelayCommand2(_ => Run());
            OpenLogCommand = new RelayCommand2(_ => OpenLog());
            SelectElemsCommand = new RelayCommand2(_ => SelectElems());
        }

        private void Reload()
        {
            var item = _store.Get(_number);
            var status = CheckStatusRules.FromItem(item);
            StatusText = CheckStatusRules.Text(status);
            StatusBrush = CheckStatusBrushes.Of(status);
            ResultTitle = string.IsNullOrWhiteSpace(item?.Title) ? _defaultResultTitle : item.Title;
            DisplayDate = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.DisplayDate
                : "—";
            LastRunBy = item?.Creator;
            ElemIds = item?.ElemIds;
        }

        private void Run()
        {
            _store.SetBusy(true);
            ChecklistRevitBridge.Enqueue(app =>
            {
                try
                {
                    var uidoc = app.ActiveUIDocument;
                    if (uidoc == null) return;

                    var result = _run(uidoc.Document);
                    string userName = app.Application.Username;
                    _dispatcher.Invoke(() => _store.ApplyRun(_number, result, userName));
                }
                catch (Exception ex)
                {
                    _dispatcher.Invoke(() =>
                        new InfoWindow280($"Не удалось выполнить проверку: {ex.Message}").ShowDialog());
                }
                finally
                {
                    _dispatcher.Invoke(() => _store.SetBusy(false));
                }
            });
        }

        private void OpenLog()
        {
            var item = _store.Get(_number);
            if (item == null || string.IsNullOrEmpty(item.LogFullPath)) return;
            try
            {
                System.Diagnostics.Process.Start("notepad.exe", item.LogFullPath + ".txt");
            }
            catch (Exception ex)
            {
                new InfoWindow400($"Не удалось открыть файл: {ex.Message}").ShowDialog();
            }
        }

        private void SelectElems()
        {
            if (string.IsNullOrWhiteSpace(ElemIds)) return;
            var parts = ElemIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            ChecklistRevitBridge.SelectElements(parts);
        }
    }
}
