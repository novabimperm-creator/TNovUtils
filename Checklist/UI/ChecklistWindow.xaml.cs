using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public partial class ChecklistWindow : Window
    {
        private readonly AutoCheckStore _store;
        private readonly BimCheckStore _bimStore;
        private readonly ChecklistWindowViewModel _vm;

        public ChecklistWindow(UIDocument uidoc)
        {
            Dispatcher.UnhandledException += OnDispatcherUnhandledException;

            InitializeComponent();

            var doc = uidoc.Document;
            SubTitle.Text = doc.Title;

            _store = new AutoCheckStore(doc);
            _bimStore = new BimCheckStore(doc);
            var registry = new CheckRegistry(_store, doc);
            _vm = new ChecklistWindowViewModel(registry, _bimStore, id => CreateView(id, registry, doc));
            DataContext = _vm;
            Closed += (s, e) =>
            {
                Dispatcher.UnhandledException -= OnDispatcherUnhandledException;
                _store.Dispose();
                _bimStore.Dispose();
                _vm.DisposeViews();
            };

            _vm.Select(CheckRegistry.SummaryId);
        }

        private System.Windows.Controls.UserControl CreateView(string id, CheckRegistry registry, Document doc)
        {
            Logger.Log("Создание представления «" + id + "»", 1);
            try
            {
                if (id == CheckRegistry.SummaryId)
                    return new SummaryControl(registry, _store, _bimStore, _vm.Select, doc);

                if (id == CheckRegistry.BimChecksId)
                    return new BimChecksControl(_bimStore);

                var check = registry.Find(id);
                if (check == null)
                {
                    Logger.Log("Проверка «" + id + "» не найдена в реестре", 4);
                    return new System.Windows.Controls.UserControl();
                }

                Logger.Log("CreateView: " + check.Title + " (" + check.Id + ")", 1);
                return check.CreateView();
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка CreateView «" + id + "»: " + ex, 4);
                new InfoWindow400("Не удалось открыть проверку:\n" + ex.Message).ShowDialog();
                return new System.Windows.Controls.UserControl();
            }
        }

        private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Log("Необработанное исключение Dispatcher: " + e.Exception, 4);
            e.Handled = true;
            try
            {
                new InfoWindow400("Ошибка чек-листа:\n" + e.Exception.Message).ShowDialog();
            }
            catch { }
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex = (ex | WS_EX_APPWINDOW) & ~(long)WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));

            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style |= WS_MINIMIZEBOX;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
