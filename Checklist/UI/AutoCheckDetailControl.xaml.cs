using System;
using TNovCommon;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public partial class AutoCheckDetailControl : System.Windows.Controls.UserControl
    {
        public AutoCheckDetailControl(
            AutoCheckStore store,
            int number,
            string headerTitle,
            string defaultResultTitle,
            Func<Autodesk.Revit.DB.Document, CheckRunResult> run)
        {
            Logger.Log("Открытие AutoCheckDetailControl #" + number + " «" + headerTitle + "»", 1);
            try
            {
                InitializeComponent();
                Logger.Log("AutoCheckDetailControl.InitializeComponent завершён", 1);
                DataContext = new AutoCheckDetailViewModel(store, number, headerTitle, defaultResultTitle, run);
                Logger.Log("AutoCheckDetailControl.DataContext назначен", 1);
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка конструктора AutoCheckDetailControl #" + number + ": " + ex, 4);
                throw;
            }
        }
    }
}
