using Autodesk.Revit.UI;
using TNovUtils.Checklist.Checks;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist
{
    /// <summary>
    /// Единственное немодальное окно чек-листа.
    /// </summary>
    public static class ChecklistHost
    {
        private static ChecklistWindow _window;

        public static void ShowOrActivate(UIApplication uiapp)
        {
            if (_window != null)
            {
                if (_window.WindowState == System.Windows.WindowState.Minimized)
                    _window.WindowState = System.Windows.WindowState.Normal;
                _window.Activate();
                return;
            }

            _window = new ChecklistWindow(uiapp.ActiveUIDocument);
            _window.Closed += (s, e) => { _window = null; };

            new System.Windows.Interop.WindowInteropHelper(_window) { Owner = uiapp.MainWindowHandle };
            _window.Show();
        }
    }
}
