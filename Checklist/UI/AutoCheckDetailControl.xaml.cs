using System;
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
            InitializeComponent();
            DataContext = new AutoCheckDetailViewModel(store, number, headerTitle, defaultResultTitle, run);
        }
    }
}
