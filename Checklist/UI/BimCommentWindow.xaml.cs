using System.Windows;
using System.Windows.Input;

namespace TNovUtils.Checklist.UI
{
    public partial class BimCommentWindow : Window
    {
        public BimCommentWindow(string checkTitle, string comment)
        {
            InitializeComponent();
            CheckTitle.Text = checkTitle ?? "";
            CommentBox.Text = comment ?? "";
            CommentBox.Focus();
            CommentBox.CaretIndex = CommentBox.Text.Length;
        }

        public string CommentText => CommentBox.Text ?? "";

        private void Save_Click(object sender, RoutedEventArgs e)
        {
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
