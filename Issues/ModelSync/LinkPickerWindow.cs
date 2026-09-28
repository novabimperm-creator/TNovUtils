// Перенесено из monumenthunny-dev/tnovpro-issues-revit (UI/LinkPickerWindow.cs) для «Модели» TNovPRO:
// вопрос о модели на сайте + синхронизация с центральной моделью (2026-09-28).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Newtonsoft.Json;
using TNovUtils.Issues.Config;

namespace TNovUtils.Issues.ModelSync
{
    /// <summary>
    /// Окно выбора связей перед выгрузкой для Атласа: что из подключённых
    /// разделов попадёт в модель.
    ///
    /// Зачем оно. Выгрузка брала ВСЕ загруженные связи и молчала про
    /// незагруженные — из-за этого в доме 76-СУЗДАЛ трижды не оказалось раздела
    /// СС (связь не была загружена в сеанс), и заметили это только через две
    /// недели. Обратная беда та же по природе: лишняя связь, подключённая по
    /// ошибке, уезжает в модель так же тихо. Здесь человек видит список целиком —
    /// с состоянием, числом экземпляров и путём к файлу — и решает сам.
    ///
    /// Собрано кодом, без .xaml — как и окно хода выгрузки: разметка потянула бы
    /// за собой ресурсы сборки.
    ///
    /// Отметки запоминаются между выгрузками (файл в %APPDATA%\TNovPROIssues):
    /// выгрузка дома — дело повторяемое, и заново снимать галочку с лишнего
    /// раздела каждый раз человек забудет ровно один раз, а узнает об этом
    /// через неделю.
    /// </summary>
    public sealed class LinkPickerWindow : Window
    {
        private const string ChoiceFile = "atlas-links.json";

        private readonly List<ModelLinks.Link> _links;
        private readonly List<CheckBox> _boxes = new List<CheckBox>();
        private readonly TextBlock _counter;

        /// <summary>Что человек отметил. Пусто — значит только основной документ.</summary>
        public IReadOnlyList<ModelLinks.Link> Chosen { get; private set; } = new List<ModelLinks.Link>();

        public LinkPickerWindow(string hostTitle, List<ModelLinks.Link> links, IntPtr revitWindow)
        {
            _links = links ?? new List<ModelLinks.Link>();

            Title = "Выгрузка для Атласа — какие разделы брать";
            Width = 720;
            MaxHeight = 760;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var skip = RecallSkipped();

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = "Основной документ: " + (hostTitle ?? "—"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            });
            root.Children.Add(new TextBlock
            {
                Text = "Он выгружается всегда. Ниже — связи: отметь те, что должны попасть в дом. "
                     + "Незагруженную связь плагин загрузит сам перед выгрузкой.",
                FontSize = 12,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            var list = new StackPanel();
            foreach (var link in _links)
            {
                link.Selected = link.Loaded && !skip.Contains(link.FileName);
                list.Children.Add(BuildRow(link));
            }

            if (_links.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "У этого документа нет связей — выгрузится он один.",
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            root.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new ScrollViewer
                {
                    MaxHeight = 460,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = list,
                },
            });

            _counter = new TextBlock
            {
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 10, 0, 10),
            };
            root.Children.Add(_counter);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(Small("Отметить все", () => SetAll(true)));
            buttons.Children.Add(Small("Снять все", () => SetAll(false)));

            var cancel = new Button
            {
                Content = "Отмена",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(16, 0, 8, 0),
                IsCancel = true,
                MinWidth = 90,
            };
            var ok = new Button
            {
                Content = "Выгрузить",
                Padding = new Thickness(14, 6, 14, 6),
                IsDefault = true,
                MinWidth = 120,
            };
            ok.Click += (s, e) =>
            {
                Chosen = _links.Where(l => l.Selected).ToList();
                RememberSkipped(_links.Where(l => !l.Selected).Select(l => l.FileName));
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            Content = root;
            UpdateCounter();

            // Владелец — главное окно Revit: иначе окно теряется за ним.
            if (revitWindow != IntPtr.Zero)
            {
                try { new WindowInteropHelper(this).Owner = revitWindow; } catch { }
            }
        }

        private UIElement BuildRow(ModelLinks.Link link)
        {
            var head = new TextBlock { Margin = new Thickness(0, 0, 0, 1), TextWrapping = TextWrapping.Wrap };
            head.Inlines.Add(new System.Windows.Documents.Run(link.Section) { FontWeight = FontWeights.Bold });
            head.Inlines.Add(new System.Windows.Documents.Run("   " + link.FileName) { Foreground = Brushes.Gray });

            var notes = new List<string> { link.Status };
            if (link.Instances > 1) notes.Add($"экземпляров: {link.Instances}");
            if (link.Nested) notes.Add("вложенная связь");
            if (!string.IsNullOrEmpty(link.Path)) notes.Add(link.Path);

            var tail = new TextBlock
            {
                Text = string.Join("  ·  ", notes),
                FontSize = 11,
                Foreground = link.Loaded ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00)),
                TextWrapping = TextWrapping.Wrap,
            };

            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(tail);

            var box = new CheckBox
            {
                Content = stack,
                IsChecked = link.Selected,
                Margin = new Thickness(0, 5, 0, 5),
                Tag = link,
            };
            box.Checked += (s, e) => { link.Selected = true; UpdateCounter(); };
            box.Unchecked += (s, e) => { link.Selected = false; UpdateCounter(); };
            _boxes.Add(box);
            return box;
        }

        private Button Small(string text, Action action)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 100,
            };
            b.Click += (s, e) => action();
            return b;
        }

        private void SetAll(bool on)
        {
            foreach (var box in _boxes) box.IsChecked = on;
            UpdateCounter();
        }

        private void UpdateCounter()
        {
            var chosen = _links.Count(l => l.Selected);
            var toLoad = _links.Count(l => l.Selected && !l.Loaded);
            var text = $"отмечено {chosen} из {_links.Count}";
            if (toLoad > 0) text += $" · будет загружено связей: {toLoad}";
            var off = _links.Count(l => !l.Selected && l.Loaded);
            if (off > 0) text += $" · пропускаем загруженных: {off}";
            _counter.Text = text;
        }

        // ——— память об отметках ———————————————————————————————————————————
        // Храним ИМЕНА СНЯТЫХ связей, а не отмеченных: новая связь в модели
        // должна попадать в выгрузку по умолчанию, а не оказаться забытой
        // потому, что её не было в прошлом списке.

        private static string ChoicePath => Path.Combine(PluginConfig.DataDir, ChoiceFile);

        private static HashSet<string> RecallSkipped()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(ChoicePath))
                {
                    var names = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(ChoicePath));
                    if (names != null) foreach (var n in names) set.Add(n);
                }
            }
            catch { /* забыли отметки — не беда, покажем всё отмеченным */ }
            return set;
        }

        private static void RememberSkipped(IEnumerable<string> names)
        {
            try { File.WriteAllText(ChoicePath, JsonConvert.SerializeObject(names.ToList())); }
            catch { /* не смогли запомнить — на выгрузку это не влияет */ }
        }
    }
}
