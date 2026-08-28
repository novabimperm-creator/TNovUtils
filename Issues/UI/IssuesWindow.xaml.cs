using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TNovUtils.Issues.Api;
using TNovUtils.Issues.Revit;

namespace TNovUtils.Issues.UI
{
    public partial class IssuesWindow : Window
    {
        private readonly ApiSession _session;
        private string _modelName;
        private string _currentModelId;
        private readonly List<Issue> _all = new List<Issue>();
        private readonly ObservableCollection<Issue> _items = new ObservableCollection<Issue>();
        private readonly Dictionary<string, string> _modelNames = new Dictionary<string, string>();
        private List<DirectoryUser> _users;                                   // каталог сотрудников (пикер ответственных)
        private Dictionary<string, string> _userNames = new Dictionary<string, string>(); // id → ФИО
        private Issue _selected;
        private bool _authed;
        private bool _ready; // фильтры не дёргаем до окончания инициализации
        private CreateIssueWindow _createWindow; // одно немодальное окно создания за раз

        private ApiClient Api => _session.Client;

        private sealed class ComboItem
        {
            public string Label { get; }
            public string Value { get; }
            public ComboItem(string label, string value) { Label = label; Value = value; }
            public override string ToString() => Label;
        }
        private sealed class ModelSlotVM { public long? ElementId { get; set; } public string SlotLabel { get; set; } }

        public IssuesWindow(ApiSession session, string modelName)
        {
            InitializeComponent();
            _session = session;
            _modelName = modelName;
            SubTitle.Text = string.IsNullOrEmpty(modelName) ? "BIM-замечания и коллизии" : $"Модель: {modelName}";
            IssuesList.ItemsSource = _items;
        }

        // WindowStyle=None снимает WS_MINIMIZEBOX; у owned-окна без WS_EX_APPWINDOW
        // нет кнопки на панели задач — тогда сворачивание превращается в «пропало».
        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex = (ex | WS_EX_APPWINDOW) & ~(long)WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));

            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style |= WS_MINIMIZEBOX;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }

        public void SetModel(string modelName)
        {
            _modelName = modelName;
            SubTitle.Text = string.IsNullOrEmpty(modelName) ? "BIM-замечания и коллизии" : $"Модель: {modelName}";
            if (_authed) _ = ReloadForModelAsync();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _authed = await EnsureAuthAsync();
            if (!_authed) { StatusBar.Text = "Не авторизовано — окно можно закрыть."; return; }
            await ReloadForModelAsync();
        }

        private async Task ReloadForModelAsync()
        {
            _ready = false;
            try
            {
                if (!string.IsNullOrEmpty(_modelName))
                {
                    var model = await Api.UpsertModelAsync(_modelName);
                    _currentModelId = model.Id;
                }
                await PopulateFiltersAsync();
                // Каталог сотрудников — для показа ФИО и пикера переназначения ответственных.
                try { _users = await Api.GetUsersAsync(); _userNames = _users.ToDictionary(u => u.Id, u => u.DisplayName); }
                catch { _users = null; }
            }
            catch (Exception ex) { StatusBar.Text = "Не удалось подготовить модель: " + ex.Message; }
            _ready = true;
            await RefreshListAsync();
        }

        private async Task PopulateFiltersAsync()
        {
            // Проекты
            var projects = new List<Project>();
            try { projects = await Api.GetProjectsAsync(false); } catch { }
            ProjectCombo.Items.Clear();
            ProjectCombo.Items.Add(new ComboItem("Все проекты", null));
            foreach (var p in projects) ProjectCombo.Items.Add(new ComboItem(p.Name, p.Id));
            ProjectCombo.SelectedIndex = 0;

            // Модели (фильтр по ИМЕНИ); по умолчанию — текущая.
            var models = new List<Model>();
            try { models = await Api.GetModelsAsync(); } catch { }
            _modelNames.Clear();
            foreach (var m in models) _modelNames[m.Id] = m.Name;
            ModelCombo.Items.Clear();
            ModelCombo.Items.Add(new ComboItem("Все модели", null));
            int sel = 0, idx = 1;
            foreach (var m in models)
            {
                ModelCombo.Items.Add(new ComboItem(m.Name, m.Name));
                if (!string.IsNullOrEmpty(_modelName) && m.Name == _modelName) sel = idx;
                idx++;
            }
            ModelCombo.SelectedIndex = sel; // текущая модель, если найдена
        }

        private async Task<bool> EnsureAuthAsync()
        {
            StatusBar.Text = "Авторизация…";
            try { if (await _session.TryRestoreSessionAsync()) return true; }
            catch { }
            StatusBar.Text = "Откройте браузер и войдите в TNovPRO…";
            try { return await _session.Browser.AuthorizeAsync(); }
            catch { return false; }
        }

        // ── Серверные фильтры (перезагрузка) ──
        private string CheckedStatus()
        {
            foreach (var child in LogicalStatusPills())
                if (child.IsChecked == true) return child.Tag as string ?? "";
            return "";
        }
        private IEnumerable<RadioButton> LogicalStatusPills()
        {
            // Пилюли лежат в WrapPanel → StackPanel; найдём по visual-tree проще через FindName не выйдет —
            // используем известные имена/перебор: PillAll + остальные через VisualTreeHelper не нужен,
            // т.к. все они GroupName="st". Соберём из StatusActions? Нет. Берём через FindVisualChildren.
            return FindChildren<RadioButton>(this).Where(r => r.GroupName == "st");
        }

        private async void StatusPill_Checked(object sender, RoutedEventArgs e) { if (_ready) await RefreshListAsync(); }
        private async void ServerFilter_Changed(object sender, RoutedEventArgs e) { if (_ready) await RefreshListAsync(); }
        private void ClientFilter_Changed(object sender, RoutedEventArgs e) { if (_ready) ApplyClientFilters(); }
        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshListAsync();

        // ── Проверка связи ──
        private async void NetCheck_Click(object sender, RoutedEventArgs e)
        {
            NetCheckButton.IsEnabled = false;
            StatusBar.Text = "Проверка связи…";
            try
            {
                var r = await Api.CheckConnectionAsync();
                string host = _session.Config.BaseUri.ToString();
                string msg;
                MessageBoxImage icon;
                if (!r.Reachable)
                {
                    msg = $"Сервер: {host}\nСвязь: НЕТ\n\n{r.Error}";
                    icon = MessageBoxImage.Error;
                    StatusBar.Text = "Связь с TNovPRO отсутствует.";
                }
                else
                {
                    string profile = r.Authorized ? "авторизован"
                        : (r.StatusCode == 401 ? "не авторизован (нужен вход)" : $"HTTP {r.StatusCode}");
                    msg = $"Сервер: {host}\nСвязь: установлена ({r.ElapsedMs} мс, HTTP {r.StatusCode})\nПрофиль: {profile}";
                    icon = MessageBoxImage.Information;
                    StatusBar.Text = r.Authorized ? "Связь с TNovPRO в порядке." : "Сервер доступен, требуется вход.";
                }
                MessageBox.Show(this, msg, "Проверка сети", MessageBoxButton.OK, icon);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Проверка сети", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { NetCheckButton.IsEnabled = true; }
        }

        // ── Выход из профиля ──
        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(this,
                "Выйти из профиля TNovPRO? Для дальнейшей работы потребуется повторный вход через браузер.",
                "Выход", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            LogoutButton.IsEnabled = false;
            StatusBar.Text = "Выход…";
            try { await _session.Auth.LogoutAsync(); }
            catch { /* всё равно закрываем — токены очищены в finally у LogoutAsync */ }
            _authed = false;
            Close(); // следующее открытие окна запустит повторную авторизацию через браузер
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (_ready) ApplyClientFilters();
        }

        private async Task RefreshListAsync()
        {
            StatusBar.Text = "Загрузка…";
            try
            {
                string status = CheckedStatus();
                string model = (ModelCombo.SelectedItem as ComboItem)?.Value;   // имя модели или null
                string projectId = (ProjectCombo.SelectedItem as ComboItem)?.Value;
                bool mine = MineToggle.IsChecked == true;

                var list = await Api.GetIssuesAsync(model, NullIfEmpty(status), projectId, mine);
                _all.Clear(); _all.AddRange(list);
                PopulatePeopleFilters();
                ApplyClientFilters();
            }
            catch (AuthRequiredException)
            {
                _authed = await EnsureAuthAsync();
                if (_authed) await RefreshListAsync();
            }
            catch (Exception ex) { StatusBar.Text = "Ошибка загрузки: " + ex.Message; }
        }

        // Авторы/ответственные — из загруженных вопросов (имён нет, показываем id).
        private void PopulatePeopleFilters()
        {
            FillPeople(AuthorCombo, "Любой автор", _all.Select(i => i.Author));
            FillPeople(AssigneeCombo, "Любой ответственный", _all.SelectMany(i => i.Assignees ?? new List<string>()));
        }
        private void FillPeople(ComboBox combo, string allLabel, IEnumerable<string> ids)
        {
            string prev = (combo.SelectedItem as ComboItem)?.Value;
            combo.Items.Clear();
            combo.Items.Add(new ComboItem(allLabel, null));
            int sel = 0, idx = 1;
            foreach (var id in ids.Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                combo.Items.Add(new ComboItem(PersonName(id), id)); // показываем ФИО, значение — id
                if (id == prev) sel = idx;
                idx++;
            }
            combo.SelectedIndex = sel;
        }

        /// <summary>ФИО по id из каталога; если каталог не загрузился/юзера нет — сам id.</summary>
        private string PersonName(string id) =>
            (!string.IsNullOrEmpty(id) && _userNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n)) ? n : id;

        // Клиентские фильтры (поиск/автор/ответственный) поверх загруженного списка.
        private void ApplyClientFilters()
        {
            string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
            string author = (AuthorCombo.SelectedItem as ComboItem)?.Value;
            string assignee = (AssigneeCombo.SelectedItem as ComboItem)?.Value;

            IEnumerable<Issue> q2 = _all;
            if (!string.IsNullOrEmpty(q)) q2 = q2.Where(i => (i.Description ?? "").ToLowerInvariant().Contains(q));
            if (!string.IsNullOrEmpty(author)) q2 = q2.Where(i => i.Author == author);
            if (!string.IsNullOrEmpty(assignee)) q2 = q2.Where(i => i.Assignees != null && i.Assignees.Contains(assignee));

            _items.Clear();
            foreach (var i in q2) _items.Add(i);
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusBar.Text = $"Вопросов: {_items.Count}" + (_items.Count != _all.Count ? $" из {_all.Count}" : "");
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
        private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 60 ? s.Substring(0, 60) + "…" : s);

        // ── Переход к элементу ──
        private void RowGo_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || b.Tag == null) return;
            var issue = _all.FirstOrDefault(x => x.Id == b.Tag.ToString());
            if (issue != null) GoToCurrentModelElement(issue);
        }
        private void GoToCurrentModelElement(Issue issue)
        {
            var slot = issue.Models?.FirstOrDefault(m => m.ModelId == _currentModelId);
            if (slot?.ElementId != null) RevitBridge.GoToElement(slot.ElementId.Value);
            else MessageBox.Show(this, "У этого вопроса нет элемента для текущей модели.", "Вопросы", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void GoToElement_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag != null && long.TryParse(b.Tag.ToString(), out long id)) RevitBridge.GoToElement(id);
            else MessageBox.Show(this, "У этой модели нет привязанного элемента.", "Вопросы", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── Деталь ──
        private async void IssuesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IssuesList.SelectedItem is Issue row) await LoadDetailAsync(row.Id);
        }
        private void ShowDetail(bool show)
        {
            DetailColumn.Width = show ? new GridLength(440) : new GridLength(0);
            DetailCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        private void CloseDetail_Click(object sender, RoutedEventArgs e) { ShowDetail(false); IssuesList.SelectedItem = null; }

        private async Task LoadDetailAsync(string id)
        {
            try
            {
                _selected = await Api.GetIssueAsync(id);
                ShowDetail(true);
                DetailHeader.Text = $"№{_selected.Number} · {Short(_selected.Description)}";
                DetailMeta.Text = $"Статус: {_selected.StatusRu}   ·   Автор: {PersonName(_selected.Author)}   ·   Изменено: {_selected.UpdatedAt}";
                DetailDescription.Text = _selected.Description ?? "";
                DetailAssignees.Text = (_selected.Assignees != null && _selected.Assignees.Count > 0)
                    ? string.Join(", ", _selected.Assignees.Select(PersonName)) : "—";

                var slots = (_selected.Models ?? new List<IssueModelRef>()).Select((m, idx) =>
                {
                    string name = (m.ModelId != null && _modelNames.TryGetValue(m.ModelId, out var n)) ? n : m.ModelId;
                    string el = m.ElementId?.ToString() ?? "—";
                    return new ModelSlotVM { ElementId = m.ElementId, SlotLabel = $"Модель {idx + 1}: {name} · элемент {el}" };
                }).ToList();
                ModelsList.ItemsSource = slots;

                BuildStatusActions(_selected);
                await LoadPhotosAsync(_selected);
            }
            catch (Exception ex) { StatusBar.Text = "Ошибка загрузки вопроса: " + ex.Message; }
        }

        private async Task LoadPhotosAsync(Issue issue)
        {
            PhotosPanel.Children.Clear();
            if (issue.Photos == null) return;
            foreach (var ph in issue.Photos)
            {
                try
                {
                    var bytes = await Api.GetBytesAsync(ph.ThumbUrl);
                    var img = new Image { Width = 96, Height = 96, Margin = new Thickness(0, 0, 6, 6), Stretch = System.Windows.Media.Stretch.UniformToFill, ToolTip = ph.Name };
                    using (var ms = new MemoryStream(bytes))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
                        img.Source = bmp;
                    }
                    PhotosPanel.Children.Add(img);
                }
                catch { }
            }
        }

        // Статус (2026-07-02): меняют ТОЛЬКО автор, ответственные и админ — остальным
        // кнопки не показываем (сервер всё равно вернёт 403 STATUS_FORBIDDEN).
        private bool CanChangeStatus(Issue issue)
        {
            var uid = _session.Tokens.CurrentUserId;
            if (string.IsNullOrEmpty(uid)) return true; // не знаем кто мы — покажем кнопки, решит сервер
            var role = _session.Tokens.CurrentUserRole ?? "";
            return issue.Author == uid ||
                   (issue.Assignees != null && issue.Assignees.Contains(uid)) ||
                   role == "admin" || role == "superadmin";
        }

        private void BuildStatusActions(Issue issue)
        {
            StatusActions.Children.Clear();
            if (!CanChangeStatus(issue))
            {
                StatusActions.Children.Add(new TextBlock
                {
                    Text = "Статус меняют автор, ответственные и администратор",
                    Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 6, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return;
            }
            void Add(string label, string target)
            {
                var b = new Button
                {
                    Content = label,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 4, 10, 4),
                    Tag = target,
                    Cursor = Cursors.Hand,
                    Style = (Style)FindResource("GhostButton"),
                };
                b.Click += Status_Click;
                StatusActions.Children.Add(b);
            }
            switch (issue.Status)
            {
                case "new": Add("Взять в работу", "in_progress"); break;
                case "in_progress": Add("Выполнено", "completed"); break;
                case "completed": Add("Закрыть", "closed"); Add("Вернуть в работу", "in_progress"); break;
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            await ApplyUpdateAsync(new Dictionary<string, object> { ["description"] = DetailDescription.Text }, "Сохранено");
        }
        private async void Status_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || !(sender is Button b)) return;
            await ApplyUpdateAsync(new Dictionary<string, object> { ["status"] = b.Tag as string }, "Статус обновлён");
        }

        // ТЗ: Ответственный — «выбор из списка», мультивыбор; меняет любой авторизованный.
        private async void EditAssignees_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            if (_users == null) { StatusBar.Text = "Список сотрудников не загрузился — обновите окно (⟳)."; return; }
            var picker = new UserPickerWindow(_users, _selected.Assignees ?? new List<string>()) { Owner = this };
            if (picker.ShowDialog() == true)
                await ApplyUpdateAsync(new Dictionary<string, object> { ["assignees"] = picker.SelectedIds }, "Ответственные обновлены");
        }

        private async Task ApplyUpdateAsync(IDictionary<string, object> changes, string okMsg)
        {
            try
            {
                var updated = await Api.UpdateIssueAsync(_selected.Id, _selected.UpdatedAt, changes);
                _selected = updated;
                StatusBar.Text = okMsg;
                await LoadDetailAsync(updated.Id);
                await RefreshListAsync();
            }
            catch (ConflictException ex) { await HandleConflictAsync(ex); }
            catch (ApiException ex)
            {
                // Содержание/ответственных правят автор и админы; статус — автор,
                // ответственные и админы (бэк отдаёт 403 с кодом).
                string msg =
                    (ex.Code == "CONTENT_FORBIDDEN" || ex.Code == "ASSIGNEES_FORBIDDEN")
                        ? "Изменять вопрос может только его автор или администратор."
                    : ex.Code == "STATUS_FORBIDDEN"
                        ? "Менять статус могут только автор, ответственные и администратор."
                    : ex.Message;
                MessageBox.Show(this, msg, "Не выполнено", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { StatusBar.Text = "Ошибка: " + ex.Message; }
        }

        private async Task HandleConflictAsync(ConflictException ex)
        {
            var r = MessageBox.Show(this, "Вопрос был изменён с момента открытия окна. Обновить данные?",
                "Конфликт изменений", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                if (ex.Current != null) await LoadDetailAsync(ex.Current.Id);
                else if (_selected != null) await LoadDetailAsync(_selected.Id);
                await RefreshListAsync();
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_modelName) || string.IsNullOrEmpty(_currentModelId))
            {
                MessageBox.Show(this, "Модель не определена — создавать вопрос не к чему.", "Вопросы", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Немодально (Show, не ShowDialog): модальный диалог в Revit блокирует обработку
            // ExternalEvent, из-за чего «Взять из выделения Revit» молча не срабатывает.
            if (_createWindow != null) { _createWindow.Activate(); return; }
            var dlg = new CreateIssueWindow(_session, _modelName, _currentModelId) { Owner = this };
            dlg.Created += async () => await RefreshListAsync();
            dlg.Closed += (_, __) => _createWindow = null;
            _createWindow = dlg;
            dlg.Show();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        // Небольшой помощник: рекурсивный обход визуального дерева.
        private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in FindChildren<T>(child)) yield return d;
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
