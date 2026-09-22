using System.Windows;
using System.Windows.Input;
using TNovCommon;

namespace TNovUtils
{
    /// <summary>
    /// Логика взаимодействия для UnpinnerWPF.xaml
    /// </summary>
    public partial class UnpinnerWPF : Window
    {
        public UnpinnerWPF(UnpinnerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
        private void acceptButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            this.Close(); // закрытие окна
        }

        private void escButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close(); // закрытие окна
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            HelpLinks.ShowHelp("Закреплятор");
        }
    }
}
