using System.Windows;

namespace TNovUtils
{
    /// <summary>
    /// Логика взаимодействия для IdSelectionWPF.xaml
    /// </summary>
    public partial class IdSelectionWPF : Window
    {
        public IdSelectionWPF(IdSelectionViewModel viewModel)
        {
            InitializeComponent();
            textBox1.Focus();
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
