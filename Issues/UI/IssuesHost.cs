using System.Windows.Interop;
using Autodesk.Revit.UI;
using TNovUtils.Issues.Api;
using TNovUtils.Issues.Config;

namespace TNovUtils.Issues.UI
{
    /// <summary>
    /// Управление единственным немодальным окном замечаний. Сессия API живёт
    /// между открытиями (сохраняет токены/cookies в памяти процесса Revit).
    /// </summary>
    public static class IssuesHost
    {
        private static IssuesWindow _window;
        private static ApiSession _session;

        /// <summary>Единая сессия API на процесс Revit (для окна вопросов и сценария «замечание из коллизии»).</summary>
        public static ApiSession Session => _session ?? (_session = new ApiSession(PluginConfig.Load()));

        public static void ShowOrActivate(UIApplication uiapp, string modelName)
        {
            if (_session == null)
                _session = new ApiSession(PluginConfig.Load());

            if (_window != null)
            {
                _window.SetModel(modelName); // модель могли переключить — обновим
                _window.Activate();
                return;
            }

            _window = new IssuesWindow(_session, modelName);
            _window.Closed += (s, e) => { _window = null; };

            // Владелец — главное окно Revit, чтобы окно вело себя как часть Revit.
            new WindowInteropHelper(_window) { Owner = uiapp.MainWindowHandle };
            _window.Show();
        }
    }
}
