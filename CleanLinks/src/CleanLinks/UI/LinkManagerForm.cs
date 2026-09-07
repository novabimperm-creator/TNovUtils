using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CleanLinks.Core;
using Form = System.Windows.Forms.Form;
using View = Autodesk.Revit.DB.View;

namespace CleanLinks.UI
{
    /// <summary>
    /// Таблица «связь → что с ней сделать». Строка на каждую RVT-связь проекта.
    ///
    /// Области действия у колонок разные, и это главное, что должно быть видно пользователю:
    /// наборы и состояние связи меняют весь проект, полутон и скрытие — только активный вид.
    /// Поэтому видовые колонки отделены цветом и подписаны именем вида.
    /// </summary>
    public class LinkManagerForm : Form
    {
        private const string Keep = "—";

        private const string GridsHide = "Скрыть";
        private const string GridsShow = "Показать";

        private const string ActionUnload = "Выгрузить";
        private const string ActionLoad = "Загрузить";
        private const string ActionReload = "Перезагрузить";

        private const string HalftoneOn = "Включить";
        private const string HalftoneOff = "Выключить";

        private const string HideOn = "Скрыть";
        private const string HideOff = "Показать";

        private const int ColName = 0;
        private const int ColStatus = 1;
        private const int ColGrids = 2;
        private const int ColWorksets = 3;
        private const int ColAction = 4;
        private const int ColHalftone = 5;
        private const int ColHide = 6;

        private static readonly Color ViewScopeColor = Color.FromArgb(245, 245, 235);

        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _summary = new Label();
        private readonly List<LinkPlan> _plans = new List<LinkPlan>();
        private bool _filling;

        public IList<LinkPlan> Plans => _plans;

        public LinkManagerForm(List<LinkInfo> links, View activeView)
        {
            string viewName = activeView != null ? activeView.Name : "нет активного вида";

            Text = "Чистые связи — связи проекта";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(940, 480);
            Size = new Size(1080, 620);
            Font = SystemFonts.MessageBoxFont;

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 66,
                Padding = new Padding(12, 10, 12, 0),
                Text = "«Оси и уровни», «Наборы» и «Действие» меняют весь проект целиком — все виды сразу.\n" +
                       "«Полутон» и «В текущем виде» действуют только в виде «" + viewName + "» " +
                       "и шаблонами не переносятся.\n" +
                       "Оси связи гасятся через её рабочие наборы: настройки категорий связи API 2022 не отдаёт."
            };

            BuildGrid(viewName);
            FillGrid(links);

            _summary.Dock = DockStyle.Bottom;
            _summary.Height = 24;
            _summary.Padding = new Padding(14, 4, 12, 0);
            _summary.ForeColor = SystemColors.GrayText;

            var apply = new Button { Text = "Применить", Width = 130, Height = 28, DialogResult = DialogResult.OK };
            apply.Click += OnApply;

            var cancel = new Button { Text = "Отмена", Width = 110, Height = 28, DialogResult = DialogResult.Cancel };

            var reset = new Button { Text = "Сбросить выбор", Width = 140, Height = 28 };
            reset.Click += OnReset;

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8, 8, 8, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(apply);
            buttons.Controls.Add(reset);

            var gridPanel = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 0, 12, 0)
            };
            gridPanel.Controls.Add(_grid);

            Controls.Add(gridPanel);
            Controls.Add(hint);
            Controls.Add(_summary);
            Controls.Add(buttons);

            AcceptButton = apply;
            CancelButton = cancel;

            UpdateSummary();
        }

        private void BuildGrid(string viewName)
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.MultiSelect = false;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            _grid.ShowCellToolTips = true;

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Связь",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 100,
                MinimumWidth = 200
            });

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Статус",
                ReadOnly = true,
                DefaultCellStyle = { ForeColor = SystemColors.GrayText }
            });

            _grid.Columns.Add(NewComboColumn("Оси и уровни", GridsHide, GridsShow));
            _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "Наборы", UseColumnTextForButtonValue = false });
            _grid.Columns.Add(NewComboColumn("Действие", ActionUnload, ActionLoad, ActionReload));

            DataGridViewComboBoxColumn halftone = NewComboColumn("Полутон", HalftoneOn, HalftoneOff);
            halftone.DefaultCellStyle.BackColor = ViewScopeColor;
            halftone.ToolTipText = "Только в виде «" + viewName + "»";
            _grid.Columns.Add(halftone);

            DataGridViewComboBoxColumn hide = NewComboColumn("В текущем виде", HideOn, HideOff);
            hide.DefaultCellStyle.BackColor = ViewScopeColor;
            hide.ToolTipText = "Только в виде «" + viewName + "»";
            _grid.Columns.Add(hide);

            _grid.CurrentCellDirtyStateChanged += OnCellDirty;
            _grid.CellValueChanged += OnCellValueChanged;
            _grid.CellContentClick += OnCellContentClick;
            _grid.DataError += OnDataError;
        }

        private static DataGridViewComboBoxColumn NewComboColumn(string header, params string[] values)
        {
            var column = new DataGridViewComboBoxColumn
            {
                HeaderText = header,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Flat,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };

            column.Items.Add(Keep);
            foreach (string value in values) column.Items.Add(value);
            return column;
        }

        private void FillGrid(List<LinkInfo> links)
        {
            _filling = true;

            foreach (LinkInfo link in links)
            {
                var plan = new LinkPlan(link);
                _plans.Add(plan);

                int index = _grid.Rows.Add();
                DataGridViewRow row = _grid.Rows[index];
                row.Tag = plan;

                row.Cells[ColName].Value = link.Name;
                row.Cells[ColStatus].Value = link.StatusText;

                SetupGridsCell(row, plan);
                SetupWorksetsCell(row, plan);
                SetupActionCell(row, link);
                SetupViewCells(row, link);
            }

            _filling = false;
        }

        private void SetupGridsCell(DataGridViewRow row, LinkPlan plan)
        {
            var cell = (DataGridViewComboBoxCell)row.Cells[ColGrids];
            LinkInfo link = plan.Link;

            if (!link.WorksetsAvailable)
            {
                Disable(cell, link.WorksetProblem);
                return;
            }

            if (link.GridWorksets.Count == 0)
            {
                Disable(cell, "в связи нет набора, похожего на оси или уровни — откройте «Наборы» и выберите вручную");
                return;
            }

            // Показываем не «ничего не выбрано», а текущее положение дел: если наборы с осями
            // уже закрыты, в ячейке сразу стоит «Скрыть», и план при этом остаётся пустым.
            cell.Value = FromGridState(plan.GridState);
            cell.ToolTipText = "Наборы: " + string.Join(", ", link.GridWorksets.Select(w => w.Name));
        }

        private void SetupWorksetsCell(DataGridViewRow row, LinkPlan plan)
        {
            var cell = (DataGridViewButtonCell)row.Cells[ColWorksets];
            cell.Value = plan.WorksetSummary();

            if (!plan.Link.WorksetsAvailable)
            {
                cell.ToolTipText = plan.Link.WorksetProblem;
                cell.Style.ForeColor = SystemColors.GrayText;
            }
            else
            {
                cell.ToolTipText = "Открыть полный список рабочих наборов связи";
            }
        }

        private void SetupActionCell(DataGridViewRow row, LinkInfo link)
        {
            var cell = (DataGridViewComboBoxCell)row.Cells[ColAction];

            if (link.IsNested)
            {
                Disable(cell, "вложенная связь — управляется из родительского файла");
                return;
            }

            cell.Items.Clear();
            cell.Items.Add(Keep);

            if (link.IsLoaded)
            {
                cell.Items.Add(ActionUnload);
                if (link.CanReload) cell.Items.Add(ActionReload);
            }
            else
            {
                cell.Items.Add(ActionLoad);
            }

            cell.Value = Keep;
        }

        private void SetupViewCells(DataGridViewRow row, LinkInfo link)
        {
            var halftone = (DataGridViewComboBoxCell)row.Cells[ColHalftone];
            var hide = (DataGridViewComboBoxCell)row.Cells[ColHide];

            if (!link.ViewGraphicsAvailable)
            {
                Disable(halftone, link.ViewProblem);
                Disable(hide, link.ViewProblem);
                return;
            }

            halftone.Value = Keep;
            halftone.ToolTipText = link.Halftone ? "Сейчас: полутон включён" : "Сейчас: полутон выключен";

            hide.Value = Keep;
            hide.ToolTipText = link.HiddenInView ? "Сейчас: скрыта в виде" : "Сейчас: видима";
        }

        private static void Disable(DataGridViewCell cell, string reason)
        {
            var combo = cell as DataGridViewComboBoxCell;
            if (combo != null)
            {
                combo.Items.Clear();
                combo.Items.Add(Keep);
                combo.DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing;
            }

            cell.Value = Keep;
            cell.ReadOnly = true;
            cell.Style.BackColor = SystemColors.Control;
            cell.Style.ForeColor = SystemColors.GrayText;
            cell.ToolTipText = reason ?? "недоступно";
        }

        /// <summary>Без этого выбор в выпадающем списке доходит до модели только после ухода фокуса.</summary>
        private void OnCellDirty(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewComboBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void OnCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_filling || e.RowIndex < 0) return;

            DataGridViewRow row = _grid.Rows[e.RowIndex];
            var plan = row.Tag as LinkPlan;
            if (plan == null) return;

            string value = Convert.ToString(row.Cells[e.ColumnIndex].Value);

            switch (e.ColumnIndex)
            {
                case ColGrids:
                    plan.SetGridState(ToGridState(value));
                    RefreshWorksetCell(row, plan);
                    break;
                case ColAction:
                    plan.Action = ToAction(value);
                    break;
                case ColHalftone:
                    plan.Halftone = ToTriState(value, HalftoneOn, HalftoneOff);
                    break;
                case ColHide:
                    plan.HideInView = ToTriState(value, HideOn, HideOff);
                    break;
                default:
                    return;
            }

            UpdateSummary();
        }

        private void OnCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColWorksets) return;

            DataGridViewRow row = _grid.Rows[e.RowIndex];
            var plan = row.Tag as LinkPlan;
            if (plan == null) return;

            if (!plan.Link.WorksetsAvailable)
            {
                MessageBox.Show(this,
                    "Рабочие наборы этой связи недоступны: " + plan.Link.WorksetProblem + ".",
                    "Чистые связи", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var form = new LinkWorksetsForm(plan))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
            }

            RefreshWorksetCell(row, plan);
            UpdateSummary();
        }

        /// <summary>Правка наборов и колонка осей — два взгляда на один план, держим их согласованными.</summary>
        private void RefreshWorksetCell(DataGridViewRow row, LinkPlan plan)
        {
            _filling = true;

            row.Cells[ColWorksets].Value = plan.WorksetSummary();

            DataGridViewCell gridsCell = row.Cells[ColGrids];
            if (!gridsCell.ReadOnly)
            {
                gridsCell.Value = FromGridState(plan.GridState);
            }

            _filling = false;
        }

        private void OnReset(object sender, EventArgs e)
        {
            _filling = true;

            foreach (DataGridViewRow row in _grid.Rows)
            {
                var plan = row.Tag as LinkPlan;
                if (plan == null) continue;

                plan.WorksetsToClose.Clear();
                plan.WorksetsToOpen.Clear();
                plan.Action = LinkStateAction.None;
                plan.Halftone = TriState.Unchanged;
                plan.HideInView = TriState.Unchanged;

                foreach (int column in new[] { ColGrids, ColAction, ColHalftone, ColHide })
                {
                    if (!row.Cells[column].ReadOnly) row.Cells[column].Value = Keep;
                }

                row.Cells[ColWorksets].Value = plan.WorksetSummary();
            }

            _filling = false;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            List<LinkPlan> active = _plans.Where(p => !p.IsEmpty).ToList();

            if (active.Count == 0)
            {
                _summary.Text = "Ничего не выбрано.";
                return;
            }

            var parts = new List<string>();

            int worksets = active.Count(p => p.HasWorksetChanges);
            if (worksets > 0) parts.Add("наборы у " + worksets + " св.");

            int actions = active.Count(p => p.Action != LinkStateAction.None);
            if (actions > 0) parts.Add("состояние у " + actions + " св.");

            int viewChanges = active.Count(p => p.HasViewChanges);
            if (viewChanges > 0) parts.Add("графика вида у " + viewChanges + " св.");

            bool reloads = active.Any(p => p.HasWorksetChanges
                                           || p.Action == LinkStateAction.Reload
                                           || p.Action == LinkStateAction.Load);

            _summary.Text = "Будет изменено: " + string.Join(", ", parts)
                            + (reloads ? ". Связи будут перезагружены — это займёт время." : ".");
        }

        private void OnApply(object sender, EventArgs e)
        {
            if (_plans.All(p => p.IsEmpty))
            {
                MessageBox.Show(this, "Выберите хотя бы одно действие.", "Чистые связи",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            List<string> unloading = _plans
                .Where(p => p.Action == LinkStateAction.Unload)
                .Select(p => "• " + p.Link.Name)
                .ToList();

            if (unloading.Count == 0) return;

            DialogResult answer = MessageBox.Show(this,
                "Будут выгружены из проекта:\n\n" + string.Join("\n", unloading)
                + "\n\nВыгруженная связь исчезнет из всех видов, пока её не загрузят обратно. Продолжить?",
                "Чистые связи", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
            {
                DialogResult = DialogResult.None;
            }
        }

        /// <summary>Пустое значение вместо ошибки: списки в ячейках разные, лишний DataError только мешает.</summary>
        private void OnDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
        }

        private static TriState ToGridState(string value)
        {
            if (value == GridsHide) return TriState.On;
            if (value == GridsShow) return TriState.Off;
            return TriState.Unchanged;
        }

        private static string FromGridState(TriState state)
        {
            switch (state)
            {
                case TriState.On: return GridsHide;
                case TriState.Off: return GridsShow;
                default: return Keep;
            }
        }

        private static TriState ToTriState(string value, string on, string off)
        {
            if (value == on) return TriState.On;
            if (value == off) return TriState.Off;
            return TriState.Unchanged;
        }

        private static LinkStateAction ToAction(string value)
        {
            switch (value)
            {
                case ActionUnload: return LinkStateAction.Unload;
                case ActionLoad: return LinkStateAction.Load;
                case ActionReload: return LinkStateAction.Reload;
                default: return LinkStateAction.None;
            }
        }
    }
}
