using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;

namespace LevelMover.UI
{
    /// <summary>
    /// Куда переносить. Один обязательный список — целевой уровень, и один необязательный —
    /// верхний, для элементов на двух уровнях. Пока уровень не выбран, «Готово» не нажать:
    /// перенос «в никуда» — самая дорогая ошибка, которую тут можно совершить.
    /// </summary>
    public class MoveElementsForm : Form
    {
        private readonly ComboBox _baseLevel = new ComboBox();
        private readonly ComboBox _topLevel = new ComboBox();
        private readonly Button _ok = new Button();

        public MoveElementsForm(IList<Level> levels, int elementCount)
        {
            if (levels == null) throw new ArgumentNullException(nameof(levels));

            Text = "Перенос элементов";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 320);
            Font = SystemFonts.MessageBoxFont;

            var selection = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(14, 9, 12, 0),
                Text = "Выбрано элементов: " + elementCount.ToString(CultureInfo.CurrentCulture)
            };

            _baseLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            _baseLevel.Location = new Point(14, 26);
            _baseLevel.Width = 372;
            _baseLevel.SelectedIndexChanged += (s, e) => _ok.Enabled = _baseLevel.SelectedItem != null;

            var target = new GroupBox
            {
                Text = "Уровень на который перенести элемент",
                Location = new Point(12, 34),
                Size = new Size(404, 68)
            };
            target.Controls.Add(_baseLevel);

            var topLabel = new Label
            {
                Text = "Верхний уровень",
                Location = new Point(14, 24),
                AutoSize = true
            };

            _topLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            _topLevel.Location = new Point(14, 46);
            _topLevel.Width = 372;

            var topHint = new Label
            {
                Text = "Используется для семейств на основе двух уровней, например стены\nи колонны. Верх останется на прежней отметке.",
                Location = new Point(14, 78),
                Size = new Size(376, 40),
                ForeColor = SystemColors.GrayText
            };

            var extras = new GroupBox
            {
                Text = "Дополнения",
                Location = new Point(12, 112),
                Size = new Size(404, 128)
            };
            extras.Controls.Add(topLabel);
            extras.Controls.Add(_topLevel);
            extras.Controls.Add(topHint);

            _ok.Text = "Готово";
            _ok.Size = new Size(110, 28);
            _ok.Location = new Point(196, 254);
            _ok.DialogResult = DialogResult.OK;
            _ok.Enabled = false;

            var cancel = new Button
            {
                Text = "Отмена",
                Size = new Size(110, 28),
                Location = new Point(314, 254),
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(target);
            Controls.Add(extras);
            Controls.Add(_ok);
            Controls.Add(cancel);
            Controls.Add(selection);

            AcceptButton = _ok;
            CancelButton = cancel;

            Fill(levels);
        }

        /// <summary>Уровень, на который переносят. Действителен только при DialogResult.OK.</summary>
        public ElementId BaseLevelId => ((LevelItem)_baseLevel.SelectedItem).Level.Id;

        /// <summary>Верхний уровень или InvalidElementId, если верхнюю зависимость не трогаем.</summary>
        public ElementId TopLevelId
        {
            get
            {
                var item = _topLevel.SelectedItem as LevelItem;
                return item == null ? ElementId.InvalidElementId : item.Level.Id;
            }
        }

        private void Fill(IList<Level> levels)
        {
            foreach (Level level in levels)
            {
                _baseLevel.Items.Add(new LevelItem(level));
                _topLevel.Items.Add(new LevelItem(level));
            }

            // Верхний уровень — дополнение, а не обязательный шаг: по умолчанию он не меняется.
            _topLevel.Items.Insert(0, NotSet);
            _topLevel.SelectedIndex = 0;
        }

        private const string NotSet = "— не менять —";

        /// <summary>
        /// Имя плюс отметка: в проектах уровни зовут «Этаж 3» и «Этаж 3 чистый пол», и без
        /// отметки их не различить.
        /// </summary>
        private class LevelItem
        {
            public LevelItem(Level level)
            {
                Level = level;
            }

            public Level Level { get; }

            public override string ToString()
            {
                double millimeters = UnitUtils.ConvertFromInternalUnits(Level.Elevation, UnitTypeId.Millimeters);
                return Level.Name + "   " + millimeters.ToString("+#,##0;-#,##0;0", CultureInfo.CurrentCulture);
            }
        }
    }
}
