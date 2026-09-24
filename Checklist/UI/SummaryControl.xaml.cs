using System;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public partial class SummaryControl : System.Windows.Controls.UserControl
    {
        public SummaryControl(CheckRegistry registry, AutoCheckStore store, BimCheckStore bimStore, Action<string> navigate, ChecklistSession session)
        {
            InitializeComponent();
            DataContext = new SummaryViewModel(registry, store, bimStore, navigate);
            DesignerHost.Content = new DesignerChecklistControl(session);
        }

        public void StopPolling()
        {
            (DesignerHost.Content as DesignerChecklistControl)?.StopPolling();
        }
    }
}
