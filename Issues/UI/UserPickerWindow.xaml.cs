using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TNovUtils.Issues.Api;

namespace TNovUtils.Issues.UI
{
    /// <summary>
    /// Модальный пикер ответственных: список пользователей платформы с поиском и
    /// чекбоксами. Возвращает выбранные Id (свойство <see cref="SelectedIds"/>).
    /// </summary>
    public partial class UserPickerWindow : Window
    {
        /// <summary>Пункт списка: пользователь + флаг «выбран» (для чекбокса).</summary>
        public sealed class PickItem : INotifyPropertyChanged
        {
            public DirectoryUser User { get; }
            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
            }
            public PickItem(DirectoryUser user, bool selected) { User = user; _isSelected = selected; }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<PickItem> _all;

        /// <summary>Id выбранных пользователей (валидно после закрытия по «Готово»).</summary>
        public List<string> SelectedIds { get; private set; }

        /// <param name="users">Полный список пользователей.</param>
        /// <param name="preselected">Id уже назначенных ответственных.</param>
        public UserPickerWindow(IEnumerable<DirectoryUser> users, IEnumerable<string> preselected)
        {
            InitializeComponent();

            var pre = new HashSet<string>(preselected ?? Enumerable.Empty<string>());
            _all = (users ?? Enumerable.Empty<DirectoryUser>())
                .OrderBy(u => u.DisplayName, System.StringComparer.CurrentCultureIgnoreCase)
                .Select(u => new PickItem(u, pre.Contains(u.Id)))
                .ToList();

            // Отслеживаем изменения чекбоксов, чтобы обновлять счётчик.
            foreach (var it in _all) it.PropertyChanged += (_, __) => UpdateCount();

            ApplyFilter("");
            StatusText.Text = _all.Count == 0 ? "Сотрудники не найдены." : $"Сотрудников: {_all.Count}";
            UpdateCount();
            Loaded += (_, __) => SearchBox.Focus();
        }

        private void ApplyFilter(string query)
        {
            query = (query ?? "").Trim();
            IEnumerable<PickItem> items = _all;
            if (query.Length > 0)
            {
                var q = query.ToLowerInvariant();
                items = _all.Where(it =>
                    (it.User.DisplayName ?? "").ToLowerInvariant().Contains(q) ||
                    (it.User.Subtitle ?? "").ToLowerInvariant().Contains(q));
            }
            UsersList.ItemsSource = new ObservableCollection<PickItem>(items);
        }

        private void UpdateCount()
        {
            CountText.Text = $"Выбрано: {_all.Count(it => it.IsSelected)}";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedIds = _all.Where(it => it.IsSelected).Select(it => it.User.Id).ToList();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
