using System.Windows;
using System.Windows.Input;

namespace TNovUtils.LinkWorksets
{
    public partial class ConfirmLinksWindow : Window
    {
        public ConfirmLinksWindow(string question, string body)
        {
            InitializeComponent();
            QuestionText.Text = question ?? "";
            BodyText.Text = body ?? "";
        }

        private void Yes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void No_Click(object sender, RoutedEventArgs e)
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
