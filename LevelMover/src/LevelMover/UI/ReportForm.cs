using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using LevelMover.Core;
using Form = System.Windows.Forms.Form;

namespace LevelMover.UI
{
    /// <summary>
    /// Что перенеслось, а что нет и почему. Показывается только когда есть непереехавшие:
    /// когда всё прошло, отчёт читать незачем.
    /// </summary>
    internal class ReportForm : Form
    {
        private readonly List<MoveResult> _skipped;

        public ReportForm(IList<MoveResult> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            _skipped = results.Where(r => !r.Moved).ToList();
            int moved = results.Count - _skipped.Count;

            Text = "Перенос элементов";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(620, 380);
            Size = new Size(720, 440);
            Font = SystemFonts.MessageBoxFont;

            var summary = new Label
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(12, 10, 12, 0),
                Text = "Перенесено элементов: " + moved.ToString(CultureInfo.CurrentCulture) + ".\n" +
                       "Остались на прежнем уровне: " + _skipped.Count.ToString(CultureInfo.CurrentCulture) + "."
            };

            var list = new ListBox
            {
                Dock = DockStyle.Fill,
                SelectionMode = SelectionMode.None,
                IntegralHeight = false,
                HorizontalScrollbar = true
            };

            foreach (MoveResult result in _skipped)
            {
                list.Items.Add(result.Description + "  —  " + result.Problem);
            }

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            body.Controls.Add(list);

            var select = new Button { Text = "Выделить в проекте", Width = 160, Height = 28 };
            select.Click += (s, e) => { SelectSkipped = true; DialogResult = DialogResult.OK; };

            var copy = new Button { Text = "Копировать список", Width = 160, Height = 28 };
            copy.Click += (s, e) => CopyToClipboard();

            var close = new Button { Text = "Закрыть", Width = 110, Height = 28, DialogResult = DialogResult.Cancel };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 46,
                Padding = new Padding(8, 9, 8, 0)
            };
            buttons.Controls.Add(close);
            buttons.Controls.Add(select);
            buttons.Controls.Add(copy);

            Controls.Add(body);
            Controls.Add(summary);
            Controls.Add(buttons);

            CancelButton = close;
        }

        /// <summary>Пользователь попросил выделить непереехавшие элементы в проекте.</summary>
        public bool SelectSkipped { get; private set; }

        private void CopyToClipboard()
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
                // Буфер обмена занят другим приложением — не повод показывать ошибку поверх отчёта.
            }
        }
    }
}
