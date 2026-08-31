using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;
using TNovCommon;
using TNovUtils.Checklist.Checks;
using TNovUtils.Checklist.Revit;

namespace TNovUtils.Checklist.UI
{
    public sealed class SummaryRow : ObservableObject
    {
        public string Id { get; }
        public string Title { get; }
        public RelayCommand2 OpenCommand { get; }

        private string _statusText;
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

        private Brush _statusBrush;
        public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

        private string _displayDate;
        public string DisplayDate { get => _displayDate; private set => SetProperty(ref _displayDate, value); }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public SummaryRow(ICheck check, Action<string> navigate)
        {
            Id = check.Id;
            Title = check.Title;
            OpenCommand = new RelayCommand2(_ => navigate(check.Id));
            Update(check);
        }

        public void Update(ICheck check)
        {
            StatusText = check.StatusText;
            StatusBrush = CheckStatusBrushes.Of(check.Status);
            DisplayDate = check.DisplayDate;
            LastRunBy = check.LastRunBy;
        }
    }

    public sealed class SummaryViewModel : ObservableObject
    {
        private readonly CheckRegistry _registry;
        private readonly AutoCheckStore _store;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private bool _isRunning;

        public ObservableCollection<SummaryRow> Rows { get; } = new ObservableCollection<SummaryRow>();
        public RelayCommand2 RunAllCommand { get; }

        public SummaryViewModel(CheckRegistry registry, AutoCheckStore store, Action<string> navigate)
        {
            _registry = registry;
            _store = store;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            foreach (var check in registry.Checks)
            {
                var row = new SummaryRow(check, navigate);
                Rows.Add(row);
                check.PropertyChanged += (s, e) => row.Update(check);
            }

            RunAllCommand = new RelayCommand2(_ => RunAll(), _ => !_isRunning);
        }

        private void RunAll()
        {
            _isRunning = true;
            RunAllCommand.RaiseCanExecuteChanged();
            _store.SetBusy(true);

            ChecklistRevitBridge.Enqueue(app =>
            {
                try
                {
                    var uidoc = app.ActiveUIDocument;
                    if (uidoc == null) return;

                    var doc = uidoc.Document;
                    string userName = app.Application.Username;
                    var results = new List<(int Number, CheckRunResult Result)>();

                    foreach (var check in _registry.Checks)
                    {
                        try
                        {
                            results.Add((check.Number, check.Run(doc)));
                        }
                        catch (Exception ex)
                        {
                            results.Add((check.Number, new CheckRunResult
                            {
                                Title = check.Title,
                                Passed = false,
                                ElemIds = "",
                                Log = $"Ошибка проверки: {ex.Message}"
                            }));
                        }
                    }

                    _dispatcher.Invoke(() => _store.ApplyRuns(results, userName));
                }
                catch (Exception ex)
                {
                    _dispatcher.Invoke(() =>
                        new InfoWindow280($"Не удалось обновить проверки: {ex.Message}").ShowDialog());
                }
                finally
                {
                    _dispatcher.Invoke(() =>
                    {
                        _store.SetBusy(false);
                        _isRunning = false;
                        RunAllCommand.RaiseCanExecuteChanged();
                    });
                }
            });
        }
    }
}
