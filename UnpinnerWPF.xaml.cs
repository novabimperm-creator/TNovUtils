using System.Windows;

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

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {

        }
    }
}
