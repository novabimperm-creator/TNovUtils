#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace TNovUtils.Checklist.Report
{
    public sealed class LevelCounter
    {
        public string Title { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Модель представления отчёта. Файл общий с TNovDesktop, поэтому без TNovCommon:
    /// своё INotifyPropertyChanged и команда, журнал — через ErrorLog.
    /// </summary>
    public sealed class ReportViewModel : INotifyPropertyChanged
    {
        /// <summary>Куда писать ошибки (в Revit — Logger.Log). Может быть null.</summary>
        public static Action<string> ErrorLog { get; set; }

        private readonly Func<string> _serverPath;
        private readonly Func<IChecklistDataSource> _source;
        private ReportResult _result;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<ModelReportRow> Rows { get; } = new ObservableCollection<ModelReportRow>();
        public ObservableCollection<LevelCounter> Counters { get; } = new ObservableCollection<LevelCounter>();

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set => Set(ref _isBusy, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        private string _warningsText = "";
        public string WarningsText
        {
            get => _warningsText;
            private set => Set(ref _warningsText, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand ExportCommand { get; }

        public ReportViewModel(string serverPath) : this(() => serverPath) { }

        /// <summary>Путь к серверу вычисляется при каждом обновлении (TNovDesktop читает конфиг).</summary>
        /// <param name="source">Источник JSON Чек-листа, тоже при каждом обновлении; null — файлы шары.</param>
        public ReportViewModel(Func<string> serverPath, Func<IChecklistDataSource> source = null)
        {
            _serverPath = serverPath;
            _source = source;
            RefreshCommand = new DelegateCommand(() => _ = RefreshAsync(), () => !IsBusy);
            ExportCommand = new DelegateCommand(Export, () => !IsBusy && Rows.Count > 0);
        }

        public async Task RefreshAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "Сбор данных…";
            try
            {
                string server = _serverPath();
                // Фабрика источника может читать шару (tnovapi.json в TNovDesktop) — не в UI-потоке.
                var result = await Task.Run(() => ModelReportBuilder.Build(server, _source?.Invoke()));
                Apply(result);
            }
            catch (Exception ex)
            {
                ErrorLog?.Invoke("Ошибка сбора отчёта: " + ex);
                StatusText = "Ошибка: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void Apply(ReportResult result)
        {
            _result = result;
            Rows.Clear();
            foreach (var row in result.Rows) Rows.Add(row);

            Counters.Clear();
            Counters.Add(Count("Автопроверки", result.Rows.Select(r => r.Auto.Level)));
            Counters.Add(Count("BIM-проверки", result.Rows.Select(r => r.Bim.Level)));
            Counters.Add(Count("NWC", result.Rows.Select(r => r.Nwc.Level)));

            StatusText = $"Период: {result.Since:dd.MM.yyyy} – {result.BuiltAt:dd.MM.yyyy HH:mm} · моделей: {result.Rows.Count}";
            WarningsText = result.Warnings.Count == 0 ? "" : string.Join("\n", result.Warnings.Take(10));
        }

        private static LevelCounter Count(string title, IEnumerable<ReportLevel> levels)
        {
            var list = levels.ToList();
            string Part(ReportLevel l) => $"{ReportLevelRules.Text(l)}: {list.Count(x => x == l)}";
            var parts = new List<string>
            {
                Part(ReportLevel.High), Part(ReportLevel.Attention), Part(ReportLevel.Norm), Part(ReportLevel.Ideal)
            };
            int na = list.Count(x => x == ReportLevel.NA);
            if (na > 0) parts.Add($"н/д: {na}");
            return new LevelCounter { Title = title, Text = string.Join(" · ", parts) };
        }

        private void Export()
        {
            if (_result == null) return;
            try
            {
                ReportExcelExporter.ExportWithDialog(_result);
            }
            catch (Exception ex)
            {
                ErrorLog?.Invoke("Ошибка выгрузки отчёта в Excel: " + ex);
                MessageBox.Show("Не удалось выгрузить отчёт в Excel:\n" + ex.Message, "Отчет",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private sealed class DelegateCommand : ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool> _canExecute;

            public DelegateCommand(Action execute, Func<bool> canExecute)
            {
                _execute = execute;
                _canExecute = canExecute;
            }

            public event EventHandler CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
            public void Execute(object parameter) => _execute();
        }
    }
}
