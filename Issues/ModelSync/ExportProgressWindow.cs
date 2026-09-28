// Перенесено из monumenthunny-dev/tnovpro-issues-revit (UI/ExportProgressWindow.cs) для «Модели» TNovPRO:
// вопрос о модели на сайте + синхронизация с центральной моделью (2026-09-28).
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TNovUtils.Issues.ModelSync
{
    /// <summary>
    /// Окно хода выгрузки: раздел, этап, полоса с процентом, время и остаток,
    /// кнопка «Прервать».
    ///
    /// Собрано кодом, без .xaml — разметка потянула бы за собой ресурсы сборки.
    ///
    /// 🔴 Главное про поток. Команда Revit выполняется в потоке интерфейса, поэтому
    /// пока идёт выгрузка, окно само не перерисуется и кнопку никто не услышит.
    /// Раскрутить очередь сообщений вручную — задача <see cref="Pump"/>, которую
    /// выгрузка зовёт по ходу дела.
    ///
    /// 2026-08-10: добавлены полоса, проценты и оценка остатка. «Долго» без цифр
    /// читается как «зависло»: непонятно, идёт ли работа и сколько ещё ждать.
    /// Остаток считается по СКОРОСТИ ТЕКУЩЕГО ЭТАПА (сколько элементов в секунду
    /// он даёт), а не по среднему за всю выгрузку: этапы разной цены, и среднее
    /// врало бы на каждом переходе.
    /// </summary>
    public sealed class ExportProgressWindow : Window
    {
        private readonly TextBlock _scope;
        private readonly TextBlock _stage;
        private readonly ProgressBar _bar;
        private readonly TextBlock _time;
        private readonly Button _cancel;
        private readonly DateTime _started = DateTime.Now;

        private bool _cancelRequested;
        private string _scopeText = "";
        private string _stageName = "";
        private DateTime _stageStarted = DateTime.Now;
        private long _stageDone, _stageTotal;
        private DateTime _lastPaint = DateTime.MinValue;

        public bool CancelRequested => _cancelRequested;
        public TimeSpan Elapsed => DateTime.Now - _started;

        public ExportProgressWindow(string title, IntPtr revitWindow)
        {
            Title = title;
            Width = 580;
            SizeToContent = System.Windows.SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Topmost = true;

            _scope = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, 0, 4),
            };
            _stage = new TextBlock
            {
                Text = "Подготовка…",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _bar = new ProgressBar
            {
                Height = 16,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                IsIndeterminate = true,          // пока счётчика нет — «работа идёт»
                Margin = new Thickness(0, 0, 0, 6),
            };
            _time = new TextBlock
            {
                Text = "прошло 0:00",
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 14),
            };
            _cancel = new Button
            {
                Content = "Прервать и сохранить собранное",
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _cancel.Click += (s, e) =>
            {
                _cancelRequested = true;
                _cancel.IsEnabled = false;
                _cancel.Content = "Останавливаюсь…";
            };

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(_scope);
            panel.Children.Add(_stage);
            panel.Children.Add(_bar);
            panel.Children.Add(_time);
            panel.Children.Add(_cancel);
            Content = panel;

            // Владелец — главное окно Revit: иначе окно теряется за ним.
            if (revitWindow != IntPtr.Zero)
            {
                try { new WindowInteropHelper(this).Owner = revitWindow; } catch { }
            }

            // Крестик окна значит то же, что кнопка: остановиться, но не бросить
            // на полпути то, что уже собрано.
            Closing += (s, e) =>
            {
                if (!_cancelRequested)
                {
                    _cancelRequested = true;
                    e.Cancel = true;
                }
            };
        }

        /// <summary>Раздел, над которым идёт работа: «раздел 3 из 12 · ОВ_С1».</summary>
        public void SetScope(int index, int count, string name)
        {
            _scopeText = count > 1
                ? $"раздел {index} из {count}" + (string.IsNullOrEmpty(name) ? "" : $" · {name}")
                : (name ?? "");
            _scope.Text = _scopeText;
            Paint(true);
        }

        public void SetStage(string text)
        {
            _stageName = text ?? "";
            _stageDone = _stageTotal = 0;
            _stageStarted = DateTime.Now;
            _bar.IsIndeterminate = true;
            _stage.Text = _stageName;
            Paint(true);
        }

        /// <summary>
        /// Этап со счётчиком. Смена названия этапа перезапускает замер скорости —
        /// иначе остаток на новом этапе считался бы по темпу предыдущего.
        /// </summary>
        public void SetStage(string stage, long done, long total)
        {
            if (stage != _stageName)
            {
                _stageName = stage;
                _stageStarted = DateTime.Now;
            }
            _stageDone = done;
            _stageTotal = total;
            Paint(false);
        }

        /// <summary>
        /// Раскрутить очередь сообщений: перерисовать окно и дать нажатой кнопке
        /// сработать. Без этого окно висит белым прямоугольником с надписью
        /// «не отвечает» — ровно тем, что мы и пытаемся убрать.
        /// </summary>
        public void Pump()
        {
            Paint(false);
        }

        /// <summary>Закрыть окончательно (в обход перехвата закрытия).</summary>
        public void ForceClose()
        {
            _cancelRequested = true;
            try { Close(); } catch { }
        }

        // Перерисовка. Дорогая часть — не сам текст, а прокрутка очереди сообщений,
        // поэтому чаще четырёх раз в секунду не рисуем: на 226 тыс. элементов
        // отрисовка на каждом отчёте съедала бы заметную долю самой выгрузки.
        private void Paint(bool force)
        {
            var now = DateTime.Now;
            if (!force && (now - _lastPaint).TotalMilliseconds < 250) return;
            _lastPaint = now;

            if (_stageTotal > 0)
            {
                var pct = (double)_stageDone / _stageTotal * 100.0;
                if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
                _bar.IsIndeterminate = false;
                _bar.Value = pct;
                _stage.Text = $"{_stageName} {_stageDone:N0} из {_stageTotal:N0}  ({pct:N0}%)";
            }
            else if (!string.IsNullOrEmpty(_stageName))
            {
                _stage.Text = _stageName;
            }

            var total = now - _started;
            var line = "прошло " + Fmt(total);
            if (_stageTotal > 0 && _stageDone > 0)
            {
                var spent = (now - _stageStarted).TotalSeconds;
                if (spent > 1.5)
                {
                    var perItem = spent / _stageDone;
                    var left = TimeSpan.FromSeconds(perItem * (_stageTotal - _stageDone));
                    line += " · осталось ~" + Fmt(left);
                    line += $" · {(_stageDone / spent):N0} элем./с";
                }
            }
            _time.Text = line;

            try { Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background); }
            catch { /* окно уже закрыто */ }
        }

        private static string Fmt(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
            return $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
