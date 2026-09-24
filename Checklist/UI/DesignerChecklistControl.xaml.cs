using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public partial class DesignerChecklistControl : System.Windows.Controls.UserControl
    {
        public DesignerChecklistControl(ChecklistSession session)
        {
            InitializeComponent();
            DataContext = new DesignerChecklistViewModel(session);
        }

        public void StopPolling()
        {
            (DataContext as DesignerChecklistViewModel)?.Dispose();
        }
    }
}
