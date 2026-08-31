using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public partial class BimChecksControl : System.Windows.Controls.UserControl
    {
        public BimChecksControl(BimCheckStore store)
        {
            InitializeComponent();
            DataContext = new BimChecksViewModel(store);
        }
    }
}
