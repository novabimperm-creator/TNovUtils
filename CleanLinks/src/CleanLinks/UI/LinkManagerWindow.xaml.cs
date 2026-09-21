using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using CleanLinks.Core;
using TNovCommon;

namespace CleanLinks.UI
{
    public partial class LinkManagerWindow : Window
    {
        private readonly LinkManagerViewModel _vm;

        public IList<LinkPlan> Plans => _vm.Plans;

        public LinkManagerWindow(List<LinkInfo> links, View activeView)
        {
            InitializeComponent();
            string viewName = activeView != null ? activeView.Name : "нет активного вида";
            HintText.Text = "«Оси и уровни», «Наборы» и «Действие» меняют весь проект. " +
                             "«Полутон» и «В текущем виде» — только вид «" + viewName + "».";
            _vm = new LinkManagerViewModel(links);
            DataContext = _vm;
        }

        private void Worksets_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as LinkRowVm;
            if (row == null) return;

            if (!row.WorksetsEnabled)
            {
                new InfoWindow280("Рабочие наборы этой связи недоступны: " + row.WorksetsToolTip)
                {
                    Owner = this
                }.ShowDialog();
                return;
            }

            var window = new LinkWorksetsWindow(row.Plan) { Owner = this };
            if (window.ShowDialog() == true)
                row.RefreshWorksets();
        }

        private void Reset_Click(object sender, RoutedEventArgs e) => _vm.Reset();

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Plans.All(p => p.IsEmpty))
            {
                new InfoWindow280("Выберите хотя бы одно действие.").ShowDialog();
                return;
            }

            List<string> unloading = _vm.Plans
                .Where(p => p.Action == LinkStateAction.Unload)
                .Select(p => "• " + p.Link.Name)
                .ToList();

            if (unloading.Count > 0)
            {
                var confirm = new TNovUtils.LinkWorksets.ConfirmLinksWindow(
                    "Выгрузить связи из проекта?",
                    "Будут выгружены:\n\n" + string.Join("\n", unloading)
                    + "\n\nВыгруженная связь исчезнет из всех видов, пока её не загрузят обратно.");
                confirm.Owner = this;
                if (confirm.ShowDialog() != true) return;
            }

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
    }
}
