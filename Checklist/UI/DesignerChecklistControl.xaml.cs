using Autodesk.Revit.DB;

namespace TNovUtils.Checklist.UI
{
    public partial class DesignerChecklistControl : System.Windows.Controls.UserControl
    {
        public DesignerChecklistControl(Document doc)
        {
            InitializeComponent();
            DataContext = new DesignerChecklistViewModel(doc);
        }

        public void StopPolling()
        {
            (DataContext as DesignerChecklistViewModel)?.Dispose();
        }
    }
}
