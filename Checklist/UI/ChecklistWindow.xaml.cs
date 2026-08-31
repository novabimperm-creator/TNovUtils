using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public partial class ChecklistWindow : Window
    {
        private readonly AutoCheckStore _store;
        private readonly ChecklistWindowViewModel _vm;

        public ChecklistWindow(UIDocument uidoc)
        {
            InitializeComponent();

            var doc = uidoc.Document;
            SubTitle.Text = doc.Title;

            _store = new AutoCheckStore(doc);
            var registry = new CheckRegistry(_store, doc);
            _vm = new ChecklistWindowViewModel(registry, id => CreateView(id, registry, doc));
            DataContext = _vm;
            Closed += (s, e) =>
            {
                _store.Dispose();
                _vm.DisposeViews();
            };

            _vm.Select(CheckRegistry.SummaryId);
        }

        private System.Windows.Controls.UserControl CreateView(string id, CheckRegistry registry, Document doc)
        {
            if (id == CheckRegistry.SummaryId)
                return new SummaryControl(registry, _store, _vm.Select, doc);

            var check = registry.Find(id);
            return check != null
                ? check.CreateView()
                : new System.Windows.Controls.UserControl();
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
