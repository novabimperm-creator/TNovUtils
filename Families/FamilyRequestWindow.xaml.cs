using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNovCommon;

namespace TNovUtils
{
    public partial class FamilyRequestWindow : Window
    {
        private string _projectPath;
        private List<string> _projectNames;

        public FamilyRequestWindow(string projectPath = null, string projectDisplayName = null)
        {
            InitializeComponent();
            LoadResponsibleList();

            // Устанавливаем срок: дата — через 2 дня от текущего момента
            dpDeadline.SelectedDate = DateTime.Now.AddDays(2).AddMinutes(5);
            txtTime.Text = DateTime.Now.ToString("HH:mm");

            // Отображаем имя и роль текущего пользователя
            string currentUser = UserNameHelper.GetCurrentUserName();
            tbUserName.Text = currentUser;
            tbNamePrefix.Text = "Имя пользователя Revit:";

            string role = RoleManager.GetCurrentUserRole();
            if (!string.IsNullOrEmpty(role))
            {
                tbUserRole.Text = role;
                tbRolePrefix.Text = "Ваша роль:";
            }
            else
            {
                tbUserRole.Text = "";
                tbRolePrefix.Text = "";
            }

            // Автоматический выбор ответственного и блокировка для не-BIM
            bool isBim = (role == "BIM");
            cmbResponsible.IsReadOnly = !isBim;

            string defaultAssignee = null;
            switch (role)
            {
                case "АР": defaultAssignee = "Чащин Е.А"; break;
                case "КР": defaultAssignee = "Порываев И.А"; break;
                case "ВК": defaultAssignee = "Рошиор А.Г"; break;
                case "ОВ": defaultAssignee = "Рошиор А.Г"; break;
                case "ЭЛ": defaultAssignee = "Рошиор А.Г"; break;
            }

            if (!string.IsNullOrEmpty(defaultAssignee))
            {
                foreach (var item in cmbResponsible.Items)
                {
                    var pair = (KeyValuePair<string, string>)item;
                    if (pair.Key == defaultAssignee)
                    {
                        cmbResponsible.SelectedItem = item;
                        break;
                    }
                }
            }
            else
            {
                if (cmbResponsible.Items.Count > 0)
                    cmbResponsible.SelectedIndex = 0;
            }

            _projectPath = projectPath ?? "Не указан";
            _projectNames = ProjectListLoader.LoadProjectNames();
            cmbProject.ItemsSource = _projectNames;

            string initialProject = null;
            if (!string.IsNullOrEmpty(RevitContext.CurrentProjectNameFromCde))
            {
                initialProject = RevitContext.CurrentProjectNameFromCde;
            }
            else if (!string.IsNullOrEmpty(projectDisplayName))
            {
                string match = _projectNames.Find(
                    n => string.Equals(n, projectDisplayName, StringComparison.OrdinalIgnoreCase));
                initialProject = match ?? projectDisplayName;
            }
            if (initialProject != null)
                cmbProject.Text = initialProject;
        }

        private void LoadResponsibleList()
        {
            cmbResponsible.ItemsSource = ResponsibleList.List;
            cmbResponsible.DisplayMemberPath = "Key";
            cmbResponsible.SelectedValuePath = "Value";
        }

        // Обработчики для кастомного заголовка окна
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void btnClearPhoto_Click(object sender, RoutedEventArgs e)
        {
            imgPreview.Source = null;
            imgPreview.Visibility = Visibility.Collapsed;
            tbPhotoHint.Visibility = Visibility.Visible;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtDescription.Text))
            {
                MessageBox.Show("Заполните описание.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (cmbResponsible.SelectedItem == null)
            {
                MessageBox.Show("Выберите ответственного.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (dpDeadline.SelectedDate == null)
            {
                MessageBox.Show("Выберите дату.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime deadline = dpDeadline.SelectedDate.Value.Add(ParseTime(txtTime.Text));
            if (deadline < DateTime.Now.AddHours(48))
            {
                MessageBox.Show("Желаемый срок не может быть раньше, чем через 48 часов от текущего момента.\nПожалуйста, измените дату или время.",
                                "Ошибка срока", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedResponsible = (KeyValuePair<string, string>)cmbResponsible.SelectedItem;
            string assigneeName = selectedResponsible.Key;

            string projectDisplayName = cmbProject.Text?.Trim() ?? "";

            var request = new RequestModel
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.Now,
                Requester = UserNameHelper.GetCurrentUserName(),
                Assignee = assigneeName,
                Deadline = deadline,
                Description = txtDescription.Text,
                Result = "",
                PhotoBase64 = ConvertImageToBase64(imgPreview.Source),
                Status = RequestStatus.Принято,
                ProjectPath = _projectPath,
                ProjectDisplayName = projectDisplayName
            };

            RequestStorage.AddRequest(request);
            //EmailNotifier.SendRequestNotification(request);

            Logger.Log($"Создана заявка {request.Description}, исполнитель: {request.Assignee}, проект: {request.ProjectDisplayName}",1);

            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private TimeSpan ParseTime(string timeStr)
        {
            return TimeSpan.TryParse(timeStr, out TimeSpan time) ? time : new TimeSpan(15, 30, 0);
        }

        private string ConvertImageToBase64(ImageSource image)
        {
            if (image == null) return null;
            if (!(image is BitmapSource bitmap)) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        imgPreview.Source = img;
                        imgPreview.Visibility = Visibility.Visible;
                        tbPhotoHint.Visibility = Visibility.Collapsed;
                    }
                }
                e.Handled = true;
            }
            base.OnPreviewKeyDown(e);
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = @"https://portal.talan.group/knowledge/proektirovanie/";
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }
    }
}