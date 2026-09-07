using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CleanLinks.Core;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;

namespace CleanLinks.UI
{
    /// <summary>
    /// Что именно выключать в связях. Выбор и подтверждение сведены в одно окно:
    /// список слева меняет содержимое предпросмотра справа, и нажимать «Выключить»
    /// пользователь может, уже видя точный перечень наборов, а не догадываясь о нём.
    /// </summary>
    public class CategoryPickerForm : Form
    {
        private readonly List<LinkInfo> _links;
        private readonly CheckedListBox _categories = new CheckedListBox();
        private readonly ListBox _preview = new ListBox();
        private readonly Label _summary = new Label();
        private readonly Button _ok = new Button();

        public List<WorksetCategory> Selected { get; private set; } = new List<WorksetCategory>();

        public CategoryPickerForm(List<LinkInfo> links)
        {
            _links = links ?? throw new ArgumentNullException(nameof(links));

            Text = "Что выключить в связях";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(660, 470);
            Size = new Size(720, 540);
            Font = SystemFonts.MessageBoxFont;

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(12, 10, 12, 0),
                Text = "Отмеченные группы будут закрыты во всех связях сразу — во всех видах, включая 3D\n" +
                       "и разрезы. Связи при этом перезагружаются."
            };

            _categories.Dock = DockStyle.Top;
            _categories.Height = 72;
            _categories.CheckOnClick = true;
            _categories.IntegralHeight = false;
            _categories.ItemCheck += OnItemCheck;

            foreach (WorksetCategory category in WorksetCategories.All)
            {
                // Оси отмечены заранее: это основной сценарий, ради которого кнопка и появилась.
                _categories.Items.Add(new Item(category), ReferenceEquals(category, WorksetCategories.Grids));
            }

            var previewLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(0, 4, 0, 0),
                Text = "Будет закрыто:"
            };

            _preview.Dock = DockStyle.Fill;
            _preview.SelectionMode = SelectionMode.None;
            _preview.IntegralHeight = false;
            _preview.HorizontalScrollbar = true;

            _summary.Dock = DockStyle.Bottom;
            _summary.Height = 40;
            _summary.Padding = new Padding(14, 4, 12, 0);
            _summary.ForeColor = SystemColors.GrayText;

            _ok.Text = "Выключить";
            _ok.Width = 130;
            _ok.Height = 28;
            _ok.DialogResult = DialogResult.OK;
            _ok.Click += OnOk;

            var cancel = new Button { Text = "Отмена", Width = 110, Height = 28, DialogResult = DialogResult.Cancel };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8, 8, 8, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            body.Controls.Add(_preview);
            body.Controls.Add(previewLabel);

            var top = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(12, 0, 12, 4) };
            top.Controls.Add(_categories);

            Controls.Add(body);
            Controls.Add(top);
            Controls.Add(hint);
            Controls.Add(_summary);
            Controls.Add(buttons);

            AcceptButton = _ok;
            CancelButton = cancel;

            RefreshPreview();
        }

        /// <summary>ItemCheck приходит до смены состояния, поэтому предпросмотр считаем следующим тиком.</summary>
        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            BeginInvoke(new Action(RefreshPreview));
        }

        private List<WorksetCategory> CheckedCategories()
        {
            return _categories.CheckedItems
                .Cast<Item>()
                .Select(i => i.Category)
                .ToList();
        }

        private void RefreshPreview()
        {
            List<WorksetCategory> categories = CheckedCategories();

            _preview.BeginUpdate();
            _preview.Items.Clear();

            int links = 0;
            int worksets = 0;

            foreach (LinkInfo link in _links.Where(l => l.WorksetsAvailable))
            {
                List<LinkWorksetInfo> matched = link.WorksetsMatching(categories);
                if (matched.Count == 0) continue;

                List<LinkWorksetInfo> toClose = matched.Where(w => w.IsOpen).ToList();
                string names = string.Join(", ", matched.Select(w => w.Name));

                if (toClose.Count == 0)
                {
                    _preview.Items.Add(link.Name + "  —  " + names + "  (уже закрыты)");
                    continue;
                }

                links++;
                worksets += toClose.Count;
                _preview.Items.Add(link.Name + "  —  " + string.Join(", ", toClose.Select(w => w.Name)));
            }

            AppendUnreachable(categories);

            _preview.EndUpdate();

            _summary.Text = links == 0
                ? "Закрывать нечего: подходящие наборы либо не найдены, либо уже закрыты."
                : "Будет закрыто наборов: " + worksets + " в " + links + " связи(ях).";

            _ok.Enabled = links > 0;
        }

        /// <summary>
        /// Связи, до которых не дотянуться, показываем прямо здесь: иначе список выглядит
        /// как полный охват проекта, а это не так.
        /// </summary>
        private void AppendUnreachable(List<WorksetCategory> categories)
        {
            var lines = new List<string>();

            foreach (LinkInfo link in _links)
            {
                if (!link.WorksetsAvailable)
                {
                    lines.Add(link.Name + "  —  " + link.WorksetProblem);
                }
                else if (categories.Count > 0 && link.WorksetsMatching(categories).Count == 0)
                {
                    lines.Add(link.Name + "  —  подходящего набора нет");
                }
            }

            if (lines.Count == 0) return;

            _preview.Items.Add(string.Empty);
            _preview.Items.Add("Не затронуто (" + lines.Count + "):");
            foreach (string line in lines) _preview.Items.Add("   " + line);
        }

        private void OnOk(object sender, EventArgs e)
        {
            Selected = CheckedCategories();

            if (Selected.Count == 0)
            {
                MessageBox.Show(this, "Отметьте хотя бы одну группу.", "Чистые связи",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            if (Selected.Any(c => ReferenceEquals(c, WorksetCategories.Grids)))
            {
                DialogResult answer = MessageBox.Show(this,
                    "Если уровни лежат в одном наборе с осями, они скроются вместе с ними. Продолжить?",
                    "Чистые связи", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes) DialogResult = DialogResult.None;
            }
        }

        private class Item
        {
            public Item(WorksetCategory category)
            {
                Category = category;
            }

            public WorksetCategory Category { get; }

            public override string ToString()
            {
                return Category.Name + " — " + Category.Hint;
            }
        }
    }
}
