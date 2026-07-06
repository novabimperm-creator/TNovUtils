using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using TNovUtils.Issues.Api;
using TNovUtils.Issues.Revit;

namespace TNovUtils.Issues.UI
{
    public partial class CreateIssueWindow : Window
    {
        private readonly ApiSession _session;
        private readonly string _modelName;
        private string _currentModelId; // путь «из коллизии» доопределяет его в Window_Loaded
        private string _projectId;
        private bool _projectResolved;
        private long? _elementId;
        private readonly List<string> _photoPaths = new List<string>();
        private List<DirectoryUser> _allUsers;              // кэш каталога сотрудников (для пикера)
        private readonly List<string> _assigneeIds = new List<string>();  // выбранные ответственные

        private ApiClient Api => _session.Client;

        /// <summary>Вопрос успешно создан. Окно немодальное (чтобы можно было выделять элементы
        /// в Revit при открытом окне), поэтому результат отдаём событием, а не DialogResult.</summary>
        public event Action Created;

        public CreateIssueWindow(ApiSession session, string modelName, string currentModelId)
        {
            InitializeComponent();
            _session = session;
            _modelName = modelName;
            _currentModelId = currentModelId;
            ModelText.Text = modelName;
            PhotosBox.ItemsSource = _photoPaths;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var model = await Api.UpsertModelAsync(_modelName); // идемпотентно: даёт id/projectId/projectResolved
                if (string.IsNullOrEmpty(_currentModelId)) _currentModelId = model.Id;
                _projectId = model.ProjectId;
                _projectResolved = model.ProjectResolved;
                if (!_projectResolved)
                {
                    var projects = await Api.GetProjectsAsync(false);
                    ProjectCombo.ItemsSource = projects;
                    ProjectPanel.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { ErrorText.Text = "Не удалось подготовить модель: " + ex.Message; }

            // Каталог сотрудников для пикера ответственных (best-effort — при ошибке
            // кнопка «Выбрать…» просто скажет, что список недоступен).
            try { _allUsers = await Api.GetUsersAsync(); }
            catch { _allUsers = null; }
        }

        private void PickAssignees_Click(object sender, RoutedEventArgs e)
        {
            if (_allUsers == null)
            {
                ErrorText.Text = "Список сотрудников не загрузился — проверьте связь и войдите заново.";
                return;
            }
            var picker = new UserPickerWindow(_allUsers, _assigneeIds) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                _assigneeIds.Clear();
                _assigneeIds.AddRange(picker.SelectedIds);
                RefreshAssigneesText();
                ErrorText.Text = "";
            }
        }

        private void RefreshAssigneesText()
        {
            if (_assigneeIds.Count == 0) { AssigneesText.Text = "не выбраны"; return; }
            var byId = (_allUsers ?? new List<DirectoryUser>()).ToDictionary(u => u.Id, u => u.DisplayName);
            var names = _assigneeIds.Select(id => byId.TryGetValue(id, out var n) ? n : id);
            AssigneesText.Text = string.Join(", ", names);
        }

        private void GrabSelection_Click(object sender, RoutedEventArgs e)
        {
            RevitBridge.ReadSelection(ids => Dispatcher.Invoke(() =>
            {
                if (ids.Count == 0) { ErrorText.Text = "В Revit ничего не выделено."; return; }
                _elementId = ids[0]; // ТЗ: элемент текущей модели = Id1
                ElementText.Text = _elementId.ToString();
                if (ids.Count > 1) ErrorText.Text = "Выбрано несколько — взят первый элемент.";
                else ErrorText.Text = "";
            }));
        }

        private void ClearElement_Click(object sender, RoutedEventArgs e)
        {
            _elementId = null;
            ElementText.Text = "не выбран";
        }

        private void AddPhotos_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = "Изображения|*.jpg;*.jpeg;*.png;*.webp|Все файлы|*.*",
            };
            if (dlg.ShowDialog(this) == true)
            {
                foreach (var f in dlg.FileNames)
                    if (!_photoPaths.Contains(f)) _photoPaths.Add(f);
                PhotosBox.Items.Refresh();
            }
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = "";
            var description = DescriptionBox.Text?.Trim();
            if (string.IsNullOrEmpty(description)) { ErrorText.Text = "Заполните содержание."; return; }

            var projectId = _projectResolved ? _projectId : (ProjectCombo.SelectedItem as Project)?.Id;
            if (!_projectResolved && string.IsNullOrEmpty(projectId)) { ErrorText.Text = "Выберите проект."; return; }

            var assignees = _assigneeIds.Distinct().ToList();

            // Текущая модель + выбранный элемент замечания.
            var models = new List<IssueModelRef>
            {
                new IssueModelRef { ModelId = _currentModelId, ElementId = _elementId },
            };

            CreateBtn.IsEnabled = false;
            try
            {
                var issue = await Api.CreateIssueAsync(models, description, assignees, projectId);
                foreach (var path in _photoPaths)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        await Api.UploadPhotoAsync(issue.Id, bytes, Path.GetFileName(path), MimeOf(path));
                    }
                    catch (Exception pe)
                    {
                        ErrorText.Text = $"Вопрос создан, но фото «{Path.GetFileName(path)}» не загрузилось: {pe.Message}";
                    }
                }

                // 3D-фрагмент (best-effort): выбранный элемент + его ближайшее окружение (соседи).
                // Геометрию даёт Revit, заливаем по контракту /geometry.
                try
                {
                    var geoIds = new List<long>();
                    if (_elementId.HasValue) geoIds.Add(_elementId.Value);

                    if (geoIds.Count > 0)
                    {
                        var glb = await ExportGeometryAsync(geoIds);
                        if (glb != null && glb.Length > 0)
                            await Api.UploadGeometryAsync(issue.Id, glb);
                    }
                }
                catch (Exception ge)
                {
                    ErrorText.Text = "Вопрос создан, но 3D-фрагмент не приложился: " + ge.Message;
                }

                Created?.Invoke();
                Close();
            }
            catch (ProjectUnresolvedException)
            {
                var projects = await Api.GetProjectsAsync(false);
                ProjectCombo.ItemsSource = projects;
                ProjectPanel.Visibility = Visibility.Visible;
                _projectResolved = false;
                ErrorText.Text = "Проект не определён — выберите вручную и повторите.";
            }
            catch (Exception ex) { ErrorText.Text = "Ошибка создания: " + ex.Message; }
            finally { CreateBtn.IsEnabled = true; }
        }

        /// <summary>Мост к Revit-экспорту .glb (ExternalEvent) как awaitable. Окно немодальное —
        /// поэтому ExternalEvent успевает отработать, пока мы ждём Task.</summary>
        private static Task<byte[]> ExportGeometryAsync(IList<long> ids)
        {
            var tcs = new TaskCompletionSource<byte[]>();
            RevitBridge.ExportGeometryGlb(ids, glb => tcs.TrySetResult(glb));
            return tcs.Task;
        }

        private static string MimeOf(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            switch (ext)
            {
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                default: return "image/jpeg";
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
