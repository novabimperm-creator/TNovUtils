using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public partial class ChecklistWindow : Window
    {
        private readonly ChecklistSession _session;
        private readonly AutoCheckStore _store;
        private readonly BimCheckStore _bimStore;
        private readonly ChecklistWindowViewModel _vm;
        private const double CollapsedOpacity = 0.55;
        private bool _isCollapsed;
        private double _restoreHeight;
        private double _restoreWidth;
        private double _restoreMinHeight;
        private double _restoreMinWidth;
        private ResizeMode _restoreResizeMode;

        public ChecklistWindow(UIDocument uidoc)
        {
            Dispatcher.UnhandledException += OnDispatcherUnhandledException;

            InitializeComponent();

            var doc = uidoc.Document;
            SubTitle.Text = doc.Title;

            // Хранилище и ключ модели — здесь (UI-поток, без сети); данные грузятся в фоне,
            // окно показывается сразу с плашкой «Загрузка…».
            _session = ChecklistSession.ForDocument(doc);
            _store = new AutoCheckStore(doc, _session);
            _bimStore = new BimCheckStore(doc, _session);
            var registry = new CheckRegistry(_store, doc);
            _vm = new ChecklistWindowViewModel(registry, _bimStore, _session, id => CreateView(id, registry, doc));
            DataContext = _vm;
            // Опрос сервера — раз в 20 с (файлы) или 10 с (API); при возврате в окно проверяем сразу, в фоне.
            Activated += (s, e) => _session.CheckNow();
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
                    return new SummaryControl(registry, _store, _bimStore, _vm.Select, _session);

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

        private void CollapseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCollapsed)
                ExpandFromStrip();
            else
                CollapseToStrip();
        }

        private void CollapseToStrip()
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

            _restoreHeight = ActualHeight;
            _restoreWidth = ActualWidth;
            _restoreMinHeight = MinHeight;
            _restoreMinWidth = MinWidth;
            _restoreResizeMode = ResizeMode;

            ContentHost.Visibility = System.Windows.Visibility.Collapsed;
            ContentRow.Height = new GridLength(0);
            MinimizeButton.Visibility = System.Windows.Visibility.Collapsed;
            CloseButton.Visibility = System.Windows.Visibility.Collapsed;
            SubTitle.Visibility = System.Windows.Visibility.Collapsed;
            TitleColumn.Width = GridLength.Auto;
            TitlePanel.Margin = new Thickness(0, 0, 12, 0);
            CollapseButton.Content = "v";
            CollapseButton.ToolTip = "Развернуть";
            CollapseButton.Margin = new Thickness(0);

            MinHeight = 0;
            MinWidth = 0;
            UpdateLayout();
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            Opacity = IsMouseOver ? 1 : CollapsedOpacity;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
            }

            _isCollapsed = true;
        }

        private void ExpandFromStrip()
        {
            SizeToContent = SizeToContent.Manual;
            ContentRow.Height = new GridLength(1, GridUnitType.Star);
            ContentHost.Visibility = System.Windows.Visibility.Visible;
            MinimizeButton.Visibility = System.Windows.Visibility.Visible;
            CloseButton.Visibility = System.Windows.Visibility.Visible;
            SubTitle.Visibility = System.Windows.Visibility.Visible;
            TitleColumn.Width = new GridLength(1, GridUnitType.Star);
            TitlePanel.Margin = new Thickness(0);
            CollapseButton.Content = "^";
            CollapseButton.ToolTip = "Свернуть в полоску";
            CollapseButton.Margin = new Thickness(0, 0, 6, 0);

            MinHeight = _restoreMinHeight;
            MinWidth = _restoreMinWidth;
            Width = _restoreWidth;
            Height = _restoreHeight;
            ResizeMode = _restoreResizeMode;
            Opacity = 1;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = 30;
                chrome.ResizeBorderThickness = new Thickness(5);
            }

            _isCollapsed = false;
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isCollapsed)
                Opacity = 1;
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isCollapsed)
                Opacity = CollapsedOpacity;
        }

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
