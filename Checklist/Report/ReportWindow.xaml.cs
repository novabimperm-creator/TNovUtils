using System.Windows;
using System.Windows.Input;
using TNovCommon;
using TNovCommon.Storage;

namespace TNovUtils.Checklist.Report
{
    /// <summary>
    /// Окно отчёта в Revit. Файл только для TNovUtils (в TNovDesktop не подключается),
    /// поэтому здесь можно TNovCommon: источник данных выбирается по TNovConfig.json.
    /// </summary>
    public partial class ReportWindow : Window
    {
        private static ReportWindow _instance;

        public ReportWindow(string serverPath)
        {
            InitializeComponent();
            var vm = new ReportViewModel(() => serverPath, () => CreateSource(serverPath));
            DataContext = vm;
            Loaded += async (s, e) => await vm.RefreshAsync();
            Closed += (s, e) => _instance = null;
        }

        /// <summary>
        /// Источник JSON Чек-листа — как DocumentStores.ForChecklist: "ChecklistStorage": "api" + ApiUrl —
        /// TNovApi (общий клиент процесса), иначе файлы шары. Конфиг перечитывается при каждом обновлении.
        /// </summary>
        private static IChecklistDataSource CreateSource(string serverPath)
        {
            TNovConfig config = TNovConfigLoad.GetCachedConfig();
            if (config != null && ChecklistDataSources.UsesApi(config.ChecklistStorage, config.ApiUrl))
                return new ApiChecklistSource(() => DocumentStores.GetClient(config));
            return new FileChecklistSource(serverPath);
        }

        /// <summary>Одно немодальное окно отчёта: повторное нажатие кнопки его активирует.</summary>
        public static void ShowOrActivate(string serverPath, System.IntPtr ownerHandle)
        {
            if (_instance != null)
            {
                if (_instance.WindowState == WindowState.Minimized)
                    _instance.WindowState = WindowState.Normal;
                _instance.Activate();
                return;
            }

            _instance = new ReportWindow(serverPath);
            new System.Windows.Interop.WindowInteropHelper(_instance) { Owner = ownerHandle };
            _instance.Show();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
