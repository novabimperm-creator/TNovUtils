using System;
using System.Drawing;
using System.Windows.Forms;
using CleanLinks.Core;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;

namespace CleanLinks.UI
{
    /// <summary>
    /// Полный список рабочих наборов одной связи. Отметка означает «набор будет закрыт»,
    /// то есть его содержимое исчезнет из всех видов проекта.
    /// Форма правит переданный план напрямую — вызывающему остаётся обновить строку таблицы.
    /// </summary>
    public class LinkWorksetsForm : Form
    {
        private readonly LinkPlan _plan;
        private readonly CheckedListBox _list = new CheckedListBox();

        public LinkWorksetsForm(LinkPlan plan)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));

            Text = "Рабочие наборы связи «" + plan.Link.Name + "»";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(460, 400);
            Size = new Size(520, 480);
            Font = SystemFonts.MessageBoxFont;

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 62,
                Padding = new Padding(12, 10, 12, 0),
                Text = "Отмеченные наборы будут закрыты — их содержимое исчезнет из всех видов.\n" +
                       "Снятая отметка у закрытого набора откроет его обратно.\n" +
                       "Связь при этом перезагружается."
            };

            _list.Dock = DockStyle.Fill;
            _list.CheckOnClick = true;
            _list.IntegralHeight = false;
            FillList();

            var ok = new Button { Text = "OK", Width = 100, Height = 28, DialogResult = DialogResult.OK };
            ok.Click += OnOk;

            var cancel = new Button { Text = "Отмена", Width = 100, Height = 28, DialogResult = DialogResult.Cancel };

            var gridsOnly = new Button { Text = "Только оси и уровни", Width = 170, Height = 28 };
            gridsOnly.Click += OnGridsOnly;

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8, 8, 8, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(gridsOnly);

            var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            listPanel.Controls.Add(_list);

            Controls.Add(listPanel);
            Controls.Add(hint);
            Controls.Add(buttons);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void FillList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();

            foreach (LinkWorksetInfo workset in _plan.Link.Worksets)
            {
                _list.Items.Add(new Item(workset), _plan.WillBeClosed(workset));
            }

            _list.EndUpdate();
        }

        /// <summary>Отмечает наборы с осями и уровнями, остальные снимает.</summary>
        private void OnGridsOnly(object sender, EventArgs e)
        {
            for (int i = 0; i < _list.Items.Count; i++)
            {
                var item = (Item)_list.Items[i];
                _list.SetItemChecked(i, item.Workset.LooksLikeGrids);
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            for (int i = 0; i < _list.Items.Count; i++)
            {
                var item = (Item)_list.Items[i];
                _plan.SetDesiredClosed(item.Workset, _list.GetItemChecked(i));
            }
        }

        private class Item
        {
            public Item(LinkWorksetInfo workset)
            {
                Workset = workset;
            }

            public LinkWorksetInfo Workset { get; }

            public override string ToString()
            {
                return Workset.IsOpen ? Workset.Name : Workset.Name + "  (сейчас закрыт)";
            }
        }
    }
}
