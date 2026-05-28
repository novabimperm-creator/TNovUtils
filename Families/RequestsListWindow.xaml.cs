using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TNovUtils
{
    public partial class RequestsListWindow : Window
    {
        private List<RequestModel> _allRequests;
        private bool _isLoaded = false;
        private bool _canViewAll;
        private bool _canEditAssignee;
        private bool _canEditStatus;

        public static List<RequestStatus> StatusList { get; } =
            Enum.GetValues(typeof(RequestStatus)).Cast<RequestStatus>().ToList();

        public RequestsListWindow()
        {
            InitializeComponent();
            _canViewAll = RoleManager.CanViewAllRequests();   // BIM видят все заявки
            _canEditAssignee = _canViewAll;                   // только BIM могут менять ответственного
            _canEditStatus = _canViewAll;                     // только BIM могут менять статус

            colAssignee.IsReadOnly = !_canEditAssignee;
            colStatus.IsReadOnly = !_canEditStatus;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            LoadRequests();
        }

        private void LoadRequests()
        {
            _allRequests = RequestStorage.LoadRequests();
            ApplyFilter();
        }

        private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoaded) ApplyFilter();
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoaded) ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (dgRequests == null) return;

            string statusFilter = (cmbStatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (statusFilter == "Все") statusFilter = null;

            string searchText = txtSearch.Text?.Trim().ToLower() ?? "";

            // Фильтр прав: BIM – все заявки, остальные – только свои
            var accessible = _canViewAll
                ? _allRequests
                : _allRequests.Where(r =>
                    string.Equals(r.Requester, UserNameHelper.GetCurrentUserName(), StringComparison.OrdinalIgnoreCase));

            var filtered = accessible.Where(r =>
                (statusFilter == null || r.Status.ToString() == statusFilter) &&
                (string.IsNullOrEmpty(searchText) ||
                 r.Id.ToLower().Contains(searchText) ||
                 r.Requester.ToLower().Contains(searchText) ||
                 r.Assignee.ToLower().Contains(searchText) ||
                 r.ProjectDisplayName.ToLower().Contains(searchText) ||
                 r.Description.ToLower().Contains(searchText))
            ).OrderByDescending(r => r.CreatedAt).ToList();

            dgRequests.ItemsSource = filtered;
        }

        private void dgRequests_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.DataContext is RequestModel request)
            {
                switch (request.Status)
                {
                    case RequestStatus.Принято:
                        e.Row.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0xB3));
                        break;
                    case RequestStatus.В_работе:
                        e.Row.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE0));
                        break;
                    case RequestStatus.На_согласовании:
                        e.Row.Background = new SolidColorBrush(Color.FromRgb(0xB3, 0xD9, 0xFF));
                        break;
                    case RequestStatus.Выполнено:
                        e.Row.Background = new SolidColorBrush(Color.FromRgb(0xB3, 0xFF, 0xB3));
                        break;
                    case RequestStatus.Закрыто:
                        e.Row.Background = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
                        break;
                    default:
                        e.Row.Background = new SolidColorBrush(Colors.White);
                        break;
                }
            }
        }

        private void dgRequests_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.Row.Item is RequestModel request)
            {
                RequestStorage.UpdateRequest(request);
                Dispatcher.BeginInvoke(new Action(() => ApplyFilter()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void dgRequests_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (dgRequests.SelectedItem is RequestModel selectedRequest)
            {
                try
                {
                    var detailsWindow = new RequestDetailsWindow(selectedRequest);
                    detailsWindow.Owner = this;
                    detailsWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось открыть детали заявки:\n" + ex.Message,
                                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadRequests();
        }

        // Кнопка закрытия в кастомном заголовке
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Перетаскивание окна за заголовок
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = @"https://portal.talan.group/knowledge/proektirovanie/eksportpdfidwgizrevit/";
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }
    }
}