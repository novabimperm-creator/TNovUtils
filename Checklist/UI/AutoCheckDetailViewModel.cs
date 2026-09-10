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
            _store.Changed += OnStoreChanged;
            try
            {
                Reload();
                Logger.Log("AutoCheckDetailViewModel Reload: " + StatusText + ", «" + ResultTitle + "»", 1);
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка Reload при открытии #" + number + ": " + ex, 4);
                throw;
            }

            RunCommand = new RelayCommand2(_ => Run());
            OpenLogCommand = new RelayCommand2(_ => OpenLog());
            SelectElemsCommand = new RelayCommand2(_ => SelectElems());
        }

        private void OnStoreChanged(object sender, EventArgs e)
        {
            try { Reload(); }
            catch (Exception ex) { Logger.Log("Ошибка Reload по Changed #" + _number + ": " + ex, 4); }
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
            Logger.Log("Запуск проверки #" + _number + " «" + HeaderTitle + "»", 1);
            _store.SetBusy(true);
            ChecklistRevitBridge.Enqueue(app =>
            {
                try
                {
                    var uidoc = app.ActiveUIDocument;
                    if (uidoc == null)
                    {
                        Logger.Log("Нет активного документа, проверка #" + _number + " пропущена", 3);
                        return;
                    }

                    var result = _run(uidoc.Document);
                    string userName = app.Application.Username;
                    Logger.Log("Проверка #" + _number + " завершена, passed=" + result.Passed, 1);
                    _dispatcher.Invoke(() => _store.ApplyRun(_number, result, userName));
                }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка выполнения проверки #" + _number + ": " + ex, 4);
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
                Logger.Log("Открытие лога проверки #" + _number + ": " + item.LogFullPath + ".txt", 2);
                System.Diagnostics.Process.Start("notepad.exe", item.LogFullPath + ".txt");
            }
            catch (Exception ex)
            {
                Logger.Log("Не удалось открыть лог проверки #" + _number + ": " + ex.Message, 4);
                new InfoWindow400($"Не удалось открыть файл: {ex.Message}").ShowDialog();
            }
        }

        private void SelectElems()
        {
            if (string.IsNullOrWhiteSpace(ElemIds)) return;
            Logger.Log("Выбор элементов проверки #" + _number + ": " + ElemIds, 1);
            var parts = ElemIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            ChecklistRevitBridge.SelectElements(parts);
        }
    }
}
