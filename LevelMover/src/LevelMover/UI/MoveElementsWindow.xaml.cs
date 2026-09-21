using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;

namespace LevelMover.UI
{
    public partial class MoveElementsWindow : Window
    {
        private const string NotSet = "— не менять —";

        public MoveElementsWindow(IList<Level> levels, int elementCount)
        {
            InitializeComponent();
            SelectionText.Text = "Выбрано элементов: " + elementCount.ToString(CultureInfo.CurrentCulture);

            foreach (Level level in levels)
            {
                var item = new LevelOption(level);
                BaseLevelBox.Items.Add(item);
                TopLevelBox.Items.Add(item);
            }

            TopLevelBox.Items.Insert(0, new LevelOption(null, NotSet));
            TopLevelBox.SelectedIndex = 0;
        }

        public ElementId BaseLevelId => ((LevelOption)BaseLevelBox.SelectedItem).Id;

        public ElementId TopLevelId => ((LevelOption)TopLevelBox.SelectedItem).Id;

        private void BaseLevel_Changed(object sender, SelectionChangedEventArgs e)
        {
            OkButton.IsEnabled = BaseLevelBox.SelectedItem is LevelOption option
                                 && option.Id != ElementId.InvalidElementId;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
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

        private sealed class LevelOption
        {
            public LevelOption(Level level)
            {
                Level = level;
                Id = level.Id;
                double millimeters = UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters);
                Display = level.Name + "   " + millimeters.ToString("+#,##0;-#,##0;0", CultureInfo.CurrentCulture);
            }

            public LevelOption(Level level, string display)
            {
                Level = level;
                Id = level == null ? ElementId.InvalidElementId : level.Id;
                Display = display;
            }

            public Level Level { get; }
            public ElementId Id { get; }
            public string Display { get; }
        }
    }
}
