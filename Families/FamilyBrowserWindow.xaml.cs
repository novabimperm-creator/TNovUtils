using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace TNovUtils
{
    public partial class FamilyBrowserWindow : Window
    {
        public FamilyInfo SelectedFamily { get; private set; }
        private List<FamilyInfo> _allFamilies;
        private ICollectionView _familiesView;
        private readonly string _libraryPath;

        public FamilyBrowserWindow(List<FamilyInfo> families, string libraryPath)
        {
            InitializeComponent();
            _libraryPath = libraryPath;
            _allFamilies = families ?? new List<FamilyInfo>();
            FamiliesGrid.ItemsSource = _allFamilies;
            _familiesView = CollectionViewSource.GetDefaultView(_allFamilies);
            _familiesView.Filter = FamilyFilter;

            var categories = _allFamilies.Select(f => f.Category)
                                         .Distinct()
                                         .OrderBy(c => c)
                                         .ToList();
            categories.Insert(0, "Все");
            CategoryFilter.ItemsSource = categories;
            CategoryFilter.SelectedIndex = 0;
        }

        private bool FamilyFilter(object obj)
        {
            if (!(obj is FamilyInfo fi)) return false;

            string search = SearchBox.Text?.Trim().ToLower() ?? "";
            string cat = CategoryFilter.SelectedItem as string;

            bool matchSearch = string.IsNullOrEmpty(search) ||
                               fi.Name.ToLower().Contains(search);
            bool matchCat = cat == null || cat == "Все" || fi.Category == cat;

            return matchSearch && matchCat;
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
            _familiesView?.Refresh();

        private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) =>
            _familiesView?.Refresh();

        private void ResetFilters_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            CategoryFilter.SelectedIndex = 0;
        }

        private async void RefreshCache_Click(object sender, RoutedEventArgs e)
        {
            var progressWindow = new Window
            {
                Title = "Обновление",
                Content = new TextBlock
                {
                    Text = "Идет обновление. Подождите, пожалуйста",
                    Margin = new Thickness(20),
                    FontSize = 14
                },
                Width = 400,
                Height = 100,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize
            };

            this.IsEnabled = false;
            progressWindow.Show();

            try
            {
                // Выполняем сканирование и получаем полный список
                var updatedFamilies = await Task.Run(() => ScanAndUpdateCache(_libraryPath));

                // Обновляем UI
                _allFamilies = updatedFamilies;
                FamiliesGrid.ItemsSource = _allFamilies;
                _familiesView = CollectionViewSource.GetDefaultView(_allFamilies);
                _familiesView.Filter = FamilyFilter;

                var categories = _allFamilies.Select(f => f.Category)
                                             .Distinct()
                                             .OrderBy(c => c)
                                             .ToList();
                categories.Insert(0, "Все");
                CategoryFilter.ItemsSource = categories;
                CategoryFilter.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при обновлении кэша: {ex.Message}", "Ошибка",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progressWindow.Close();
                this.IsEnabled = true;
            }
        }

        private void RefreshCacheInternal(string rootPath)
        {
            var familyFolders = Directory.GetDirectories(rootPath, "*_Семейства*", SearchOption.TopDirectoryOnly);

            Parallel.ForEach(familyFolders, folder =>
            {
                string category = Path.GetFileName(folder);
                var rfaFiles = SafeGetAllRfaFiles(folder);
                foreach (string file in rfaFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.Contains("000"))
                            continue;

                        FileInfo fi = new FileInfo(file);
                        DateTime lastModified = fi.LastWriteTime;
                        FamilyCacheManager.UpdateOrCreateItem(file, category, lastModified);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Пропущен файл {file}: {ex.Message}");
                    }
                }
            });
        }
        private List<FamilyInfo> ScanAndUpdateCache(string rootPath)
        {
            // 1. Загружаем текущий кэш один раз
            var existingCache = FamilyCacheManager.LoadCache();
            var cacheDict = new ConcurrentDictionary<string, FamilyCacheItem>(existingCache);

            // 2. Список семейств, который вернём в UI
            var result = new ConcurrentBag<FamilyInfo>();

            // 3. Получаем папки *_Семейства*
            var familyFolders = Directory.GetDirectories(rootPath, "*_Семейства*", SearchOption.TopDirectoryOnly);

            // 4. Параллельное сканирование (обновляем только ConcurrentDictionary)
            Parallel.ForEach(familyFolders, folder =>
            {
                string category = Path.GetFileName(folder);
                var rfaFiles = SafeGetAllRfaFiles(folder);
                foreach (string file in rfaFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.Contains("000"))
                            continue;

                        FileInfo fi = new FileInfo(file);
                        DateTime lastModified = fi.LastWriteTime;

                        // Добавляем или обновляем запись в кэше
                        var item = cacheDict.GetOrAdd(file, key => new FamilyCacheItem
                        {
                            FullPath = key,
                            Name = Path.GetFileNameWithoutExtension(key),
                            Category = category,
                            VersionNumber = 0,
                            VersionString = "v0"
                        });

                        lock (item) // синхронизация изменения конкретного элемента, если нужно
                        {
                            if (item.LastModified != lastModified)
                            {
                                item.LastModified = lastModified;
                                item.VersionNumber++;
                                item.VersionString = $"v{item.VersionNumber}";
                            }
                            item.Category = category; // на случай, если категория изменилась
                        }

                        // Добавляем FamilyInfo для UI
                        result.Add(new FamilyInfo
                        {
                            FullPath = file,
                            Name = item.Name,
                            Version = item.VersionString,
                            LastModified = lastModified,
                            Category = category,
                            VersionNumber = item.VersionNumber
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Пропущен файл {file}: {ex.Message}");
                    }
                }
            });

            // 5. Сохраняем обновлённый кэш на диск (один раз)
            var finalDict = new Dictionary<string, FamilyCacheItem>(cacheDict);
            FamilyCacheManager.SaveCache(finalDict);

            return result.ToList();
        }

        private List<string> SafeGetAllRfaFiles(string rootPath)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                string currentDir = stack.Pop();
                try
                {
                    foreach (string subDir in Directory.GetDirectories(currentDir))
                        stack.Push(subDir);

                    foreach (string file in Directory.GetFiles(currentDir, "*.rfa"))
                    {
                        if (file.Length < 260)
                            result.Add(file);
                    }
                }
                catch (PathTooLongException) { }
                catch (UnauthorizedAccessException) { }
                catch { }
            }
            return result;
        }

        private void LoadBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedFamily = FamiliesGrid.SelectedItem as FamilyInfo;
            if (SelectedFamily == null)
            {
                MessageBox.Show("Выберите семейство из списка.",
                                "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void RequestBtn_Click(object sender, RoutedEventArgs e)
        {
            var requestWindow = new FamilyRequestWindow(
                RevitContext.CurrentProjectPath,
                RevitContext.CurrentProjectDisplayName);
            requestWindow.ShowDialog();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = @"https://portal.talan.group/knowledge/proektirovanie/eksportpdfidwgizrevit/";
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }
    }
}