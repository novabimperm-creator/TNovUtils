using System.Windows;

namespace TNovUtils
{
    /// <summary>
    /// Логика взаимодействия для PLWWPF.xaml
    /// </summary>
    public partial class PLWWPF : Window
    {
        public PLWWPF(PLWViewModel viewModel)
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
