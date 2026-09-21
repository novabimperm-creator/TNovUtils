using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LevelMover.Core;

namespace LevelMover.UI
{
    public partial class MoveReportWindow : Window
    {
        private readonly List<MoveResult> _skipped;

        public MoveReportWindow(IList<MoveResult> results)
        {
            InitializeComponent();

            _skipped = results.Where(r => !r.Moved).ToList();
            int moved = results.Count - _skipped.Count;

            SummaryText.Text = "Перенесено элементов: " + moved.ToString(CultureInfo.CurrentCulture) + ".\n" +
                                "Остались на прежнем уровне: " + _skipped.Count.ToString(CultureInfo.CurrentCulture) + ".";

            foreach (MoveResult result in _skipped)
            {
                SkippedList.Items.Add(result.Description + "  —  " + result.Problem);
            }
        }

        public bool SelectSkipped { get; private set; }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            SelectSkipped = true;
            DialogResult = true;
            Close();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            string text = string.Join(
                Environment.NewLine,
                _skipped.Select(r => r.Description + " — " + r.Problem));

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception)
            {
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
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
