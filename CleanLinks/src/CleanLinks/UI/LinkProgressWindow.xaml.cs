using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CleanLinks.Core;

namespace CleanLinks.UI
{
    public partial class LinkProgressWindow : Window, IProgressReporter, IDisposable
    {
        public bool IsCancelled { get; private set; }

        public LinkProgressWindow()
        {
            InitializeComponent();
        }

        public void Report(string caption, int index, int total)
        {
            Bar.Maximum = Math.Max(total, 1);
            Bar.Value = Math.Min(Math.Max(index - 1, 0), Bar.Maximum);
            CaptionText.Text = caption ?? "";
            DetailText.Text = "";
            CounterText.Text = "Связь " + index + " из " + total;
            Pump();
        }

        public void ReportDetail(string detail)
        {
            DetailText.Text = detail ?? "";
            Pump();
        }

        public void Finish()
        {
            Bar.Value = Bar.Maximum;
            DetailText.Text = "готово";
            Pump();
        }

        public void Dispose()
        {
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            IsCancelled = true;
            ((Button)sender).IsEnabled = false;
            DetailText.Text = "прерываю после текущей связи…";
            Pump();
        }

        private void Pump()
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }
    }
}
