using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TNovUtils
{
    public partial class RequestDetailsWindow : Window
    {
        public RequestDetailsWindow(RequestModel request)
        {
            InitializeComponent();

            // Заполняем текстовые поля
            tbId.Text = request.Id;
            tbDate.Text = request.CreatedAt.ToString("dd.MM.yyyy HH:mm");
            tbRequester.Text = request.Requester;
            tbAssignee.Text = request.Assignee;
            tbProject.Text = request.ProjectDisplayName;
            tbDeadline.Text = request.Deadline?.ToString("dd.MM.yyyy HH:mm") ?? "не указан";
            tbDescription.Text = request.Description;

            // Статус с цветом
            tbStatus.Text = request.Status.ToString();
            switch (request.Status)
            {
                case RequestStatus.Принято:
                    tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0xB3));
                    break;
                case RequestStatus.В_работе:
                    tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE0));
                    break;
                case RequestStatus.На_согласовании:
                    tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0xD9, 0xFF));
                    break;
                case RequestStatus.Выполнено:
                    tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0xFF, 0xB3));
                    break;
                case RequestStatus.Закрыто:
                    tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
                    break;
            }

            // Фото
            if (!string.IsNullOrEmpty(request.PhotoBase64))
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(request.PhotoBase64);
                    using (var ms = new MemoryStream(bytes))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        imgPhoto.Source = bitmap;
                        tbNoPhoto.Visibility = Visibility.Collapsed;
                    }
                }
                catch
                {
                    tbNoPhoto.Visibility = Visibility.Visible;
                }
            }
            else
            {
                tbNoPhoto.Visibility = Visibility.Visible;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
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