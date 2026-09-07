using System;
using System.Drawing;
using System.Windows.Forms;
using CleanLinks.Core;
using Form = System.Windows.Forms.Form;

namespace CleanLinks.UI
{
    /// <summary>
    /// Окно хода работы: полоса, имя текущей связи и что с ней сейчас делают.
    ///
    /// Перезагрузка связи идёт в основном потоке Revit — вынести её в фон нельзя, API Revit
    /// однопоточный. Поэтому окно перерисовывается вручную через Refresh + DoEvents: без этого
    /// оно застынет белым прямоугольником на всё время операции.
    /// </summary>
    public class ProgressForm : Form, IProgressReporter
    {
        private readonly Label _caption = new Label();
        private readonly Label _detail = new Label();
        private readonly Label _counter = new Label();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Button _cancel = new Button();
        private bool _cancelled;

        public bool IsCancelled => _cancelled;

        public ProgressForm(string title)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 156);
            Font = SystemFonts.MessageBoxFont;

            _caption.SetBounds(16, 16, 488, 20);
            _caption.AutoEllipsis = true;
            _caption.Font = new Font(Font, FontStyle.Bold);

            _detail.SetBounds(16, 40, 488, 20);
            _detail.AutoEllipsis = true;
            _detail.ForeColor = SystemColors.GrayText;

            _bar.SetBounds(16, 68, 488, 20);
            _bar.Minimum = 0;
            _bar.Maximum = 1;

            _counter.SetBounds(16, 94, 300, 20);
            _counter.ForeColor = SystemColors.GrayText;

            _cancel.SetBounds(384, 116, 120, 28);
            _cancel.Text = "Прервать";
            _cancel.Click += OnCancel;

            Controls.Add(_caption);
            Controls.Add(_detail);
            Controls.Add(_bar);
            Controls.Add(_counter);
            Controls.Add(_cancel);
        }

        public void Report(string caption, int index, int total)
        {
            _bar.Maximum = Math.Max(total, 1);
            _bar.Value = Math.Min(Math.Max(index - 1, 0), _bar.Maximum);
            _caption.Text = caption;
            _detail.Text = string.Empty;
            _counter.Text = "Связь " + index + " из " + total;
            Pump();
        }

        public void ReportDetail(string detail)
        {
            _detail.Text = detail;
            Pump();
        }

        /// <summary>Отмечает, что текущий шаг закончен — полоса доходит до правого края в конце.</summary>
        public void Finish()
        {
            _bar.Value = _bar.Maximum;
            _detail.Text = "готово";
            Pump();
        }

        private void OnCancel(object sender, EventArgs e)
        {
            _cancelled = true;
            _cancel.Enabled = false;
            _detail.Text = "прерываю после текущей связи…";
            Pump();
        }

        private void Pump()
        {
            Refresh();
            Application.DoEvents();
        }

        /// <summary>
        /// Показывает окно поверх главного окна Revit. Без владельца оно уходит за Revit
        /// и пользователь видит только зависший интерфейс.
        /// </summary>
        public void ShowOver(IntPtr revitMainWindow)
        {
            if (revitMainWindow == IntPtr.Zero) Show();
            else Show(new WindowHandle(revitMainWindow));

            Pump();
        }

        private class WindowHandle : IWin32Window
        {
            public WindowHandle(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}
