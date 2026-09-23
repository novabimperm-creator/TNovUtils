using System.Windows;
using System.Windows.Input;

namespace TNovUtils.Checklist.Report
{
    public partial class ReportWindow : Window
    {
        private static ReportWindow _instance;

        public ReportWindow(string serverPath)
        {
            InitializeComponent();
            var vm = new ReportViewModel(serverPath);
            DataContext = vm;
            Loaded += async (s, e) => await vm.RefreshAsync();
            Closed += (s, e) => _instance = null;
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
