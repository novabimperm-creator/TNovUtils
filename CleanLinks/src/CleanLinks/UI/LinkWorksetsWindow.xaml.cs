using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using CleanLinks.Core;

namespace CleanLinks.UI
{
    public partial class LinkWorksetsWindow : Window
    {
        private readonly LinkPlan _plan;
        private readonly List<WorksetItem> _items = new List<WorksetItem>();

        public LinkWorksetsWindow(LinkPlan plan)
        {
            _plan = plan;
            InitializeComponent();
            TitleText.Text = "НАБОРЫ «" + plan.Link.Name.ToUpperInvariant() + "»";

            foreach (LinkWorksetInfo workset in plan.Link.Worksets)
            {
                _items.Add(new WorksetItem
                {
                    Workset = workset,
                    Display = workset.IsOpen ? workset.Name : workset.Name + "  (сейчас закрыт)",
                    IsChecked = plan.WillBeClosed(workset)
                });
            }

            WorksetList.ItemsSource = _items;
        }

        private void GridsOnly_Click(object sender, RoutedEventArgs e)
        {
            foreach (WorksetItem item in _items)
                item.IsChecked = item.Workset.LooksLikeGrids;
            WorksetList.Items.Refresh();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            foreach (WorksetItem item in _items)
                _plan.SetDesiredClosed(item.Workset, item.IsChecked);
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private sealed class WorksetItem
        {
            public LinkWorksetInfo Workset { get; set; }
            public string Display { get; set; }
            public bool IsChecked { get; set; }
        }
    }
}
