using Autodesk.Revit.DB;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using TNovCommon;

namespace TNovUtils.Checklist.UI
{
    public sealed class DesignerChecklistViewModel : ObservableObject, IDisposable
    {
        private readonly string _jsonPath;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private bool _isEditing;
        private bool _disposed;

        public ObservableCollection<CheckItem> Items { get; }
        public ICollectionView ItemsView { get; }
        public ObservableCollection<string> Creators { get; } = new ObservableCollection<string>();

        private string _selectedCreator = "Все";
        public string SelectedCreator
        {
            get => _selectedCreator;
            set { if (SetProperty(ref _selectedCreator, value)) ApplyFilter(); }
        }

        private string _newTaskText;
        public string NewTaskText
        {
            get => _newTaskText;
            set => SetProperty(ref _newTaskText, value);
        }

        public RelayCommand2 AddCommand { get; }
        public RelayCommand2 RemoveCommand { get; }
        public RelayCommand2 EditTitleCommand { get; }
        public RelayCommand2 PastePhotoCommand { get; }
        public RelayCommand2 DeletePhotoCommand { get; }
        public RelayCommand2 ViewPhotoCommand { get; }

        private string PhotosRootFolder
        {
            get
            {
                if (string.IsNullOrEmpty(_jsonPath)) return null;
                string folder = Path.Combine(Path.GetDirectoryName(_jsonPath),
                    Path.GetFileNameWithoutExtension(_jsonPath) + "_photos");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        public DesignerChecklistViewModel(Document doc)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _jsonPath = JsonDataService.GetJsonPath(doc, "checklist");

            try
            {
                var data = JsonDataService.Load(_jsonPath);
                foreach (var item in data)
                {
                    if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
                    item.SetPhotosRootFolder(PhotosRootFolder);
                    SubscribeItem(item);
                }
                Items = new ObservableCollection<CheckItem>(data);
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось загрузить чек-лист: {ex.Message}").ShowDialog();
                Items = new ObservableCollection<CheckItem>();
            }

            ItemsView = CollectionViewSource.GetDefaultView(Items);
            ItemsView.SortDescriptions.Add(new SortDescription(nameof(CheckItem.IsChecked), ListSortDirection.Ascending));
            ItemsView.SortDescriptions.Add(new SortDescription(nameof(CheckItem.CreationDate), ListSortDirection.Descending));
            ApplyFilter();

            Items.CollectionChanged += (s, e) => UpdateCreators();
            UpdateCreators();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (s, e) => Poll();
            _timer.Start();

            AddCommand = new RelayCommand2(_ => AddItem());
            RemoveCommand = new RelayCommand2(obj => RemoveItem(obj as CheckItem));
            EditTitleCommand = new RelayCommand2(obj => EditTitle(obj as CheckItem));
            PastePhotoCommand = new RelayCommand2(obj => PastePhoto(obj as CheckItem));
            DeletePhotoCommand = new RelayCommand2(obj => DeletePhoto(obj as CheckItem));
            ViewPhotoCommand = new RelayCommand2(obj => ViewPhoto(obj as CheckItem));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }

        private void AddItem()
        {
            if (string.IsNullOrWhiteSpace(NewTaskText)) return;

            _isEditing = true;
            string userName = RevitAPI.UiApplication?.Application?.Username ?? "";
            var newItem = new CheckItem
            {
                Title = NewTaskText,
                IsChecked = false,
                Creator = userName,
                CreationDate = DateTime.Now
            };
            newItem.SetPhotosRootFolder(PhotosRootFolder);
            SubscribeItem(newItem);
            Items.Add(newItem);
            ItemsView.Refresh();
            NewTaskText = string.Empty;
            SaveData();
            _isEditing = false;
        }

        private void RemoveItem(CheckItem item)
        {
            if (item == null) return;

            var qViewModel = new QuestionWindowViewModel
            {
                headtxt = "Элемент можно отметить выполненным. Действительно удалить элемент?"
            };
            var qwpfview = new QuestionWindow280(qViewModel);
            qViewModel.CloseRequest += (s, e) => qwpfview.Close();
            if (qwpfview.ShowDialog() != true) return;

            _isEditing = true;
            string itemPhotoFolder = Path.Combine(PhotosRootFolder, item.Id.ToString());
            if (Directory.Exists(itemPhotoFolder))
            {
                try { Directory.Delete(itemPhotoFolder, true); }
                catch { }
            }
            item.PropertyChanged -= Item_PropertyChanged;
            Items.Remove(item);
            ItemsView.Refresh();
            SaveData();
            _isEditing = false;
        }

        private void EditTitle(CheckItem item)
        {
            if (item == null) return;

            var viewModel = new InfoWindowTextFieldViewModel
            {
                headtxt = "Введите новое название замечания:",
                ids = item.Title,
                lowtxt = ""
            };
            var window = new InfoWindowTextField(viewModel);
            if (window.ShowDialog() != true) return;

            string newTitle = viewModel.ids;
            if (!string.IsNullOrWhiteSpace(newTitle) && newTitle != item.Title)
            {
                item.Title = newTitle;
                SaveData();
            }
        }

        private void PastePhoto(CheckItem item)
        {
            if (item == null) return;
            if (string.IsNullOrEmpty(PhotosRootFolder))
            {
                new InfoWindow280("Не задана корневая папка для фотографий.").ShowDialog();
                return;
            }

            try
            {
                if (!System.Windows.Forms.Clipboard.ContainsImage())
                {
                    new InfoWindow280("Буфер обмена не содержит изображения.").ShowDialog();
                    return;
                }

                using (var bitmap = System.Windows.Forms.Clipboard.GetImage())
                {
                    if (bitmap == null)
                    {
                        new InfoWindow280("Не удалось извлечь изображение из буфера обмена.").ShowDialog();
                        return;
                    }

                    string itemFolder = Path.Combine(PhotosRootFolder, item.Id.ToString());
                    Directory.CreateDirectory(itemFolder);

                    string newFileName = $"{Guid.NewGuid()}.png";
                    string newPath = Path.Combine(itemFolder, newFileName);
                    bitmap.Save(newPath, ImageFormat.Png);

                    string oldPath = null;
                    if (!string.IsNullOrEmpty(item.PhotoFileName))
                        oldPath = Path.Combine(itemFolder, item.PhotoFileName);

                    item.PhotoFileName = newFileName;
                    SaveData();
                    ItemsView.Refresh();

                    if (oldPath != null && File.Exists(oldPath))
                    {
                        try { File.Delete(oldPath); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось вставить фото: {ex.Message}").ShowDialog();
            }
        }

        private void DeletePhoto(CheckItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.PhotoFileName)) return;
            try
            {
                string itemFolder = Path.Combine(PhotosRootFolder, item.Id.ToString());
                string filePath = Path.Combine(itemFolder, item.PhotoFileName);
                if (File.Exists(filePath))
                    File.Delete(filePath);
                item.PhotoFileName = null;
                SaveData();
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось удалить фото: {ex.Message}").ShowDialog();
            }
        }

        private void ViewPhoto(CheckItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.PhotoFullPath) || !File.Exists(item.PhotoFullPath))
                return;
            try
            {
                var proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = item.PhotoFullPath;
                proc.StartInfo.UseShellExecute = true;
                proc.Start();
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось открыть фото: {ex.Message}").ShowDialog();
            }
        }

        private void SubscribeItem(CheckItem item) => item.PropertyChanged += Item_PropertyChanged;

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SaveData();
            if (e.PropertyName == nameof(CheckItem.Creator))
                UpdateCreators();
        }

        private void ApplyFilter()
        {
            if (ItemsView == null) return;
            ItemsView.Filter = obj =>
            {
                if (!(obj is CheckItem item)) return false;
                if (string.IsNullOrEmpty(SelectedCreator) || SelectedCreator == "Все")
                    return true;
                return item.Creator == SelectedCreator;
            };
            ItemsView.Refresh();
        }

        private void UpdateCreators()
        {
            var unique = Items
                .Select(i => i.Creator)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            Creators.Clear();
            Creators.Add("Все");
            foreach (var creator in unique)
                Creators.Add(creator);

            if (!string.IsNullOrEmpty(SelectedCreator) &&
                SelectedCreator != "Все" &&
                !Creators.Contains(SelectedCreator))
            {
                SelectedCreator = "Все";
            }
        }

        private void Poll()
        {
            if (_isEditing || _disposed || string.IsNullOrEmpty(_jsonPath)) return;

            int localCount = Items.Count;
            Task.Run(() =>
            {
                try
                {
                    var serverItems = JsonDataService.Load(_jsonPath);
                    _dispatcher.Invoke(() =>
                    {
                        if (_isEditing || _disposed) return;
                        if (serverItems.Count == localCount) return;

                        foreach (var item in Items)
                            item.PropertyChanged -= Item_PropertyChanged;
                        Items.Clear();
                        foreach (var item in serverItems)
                        {
                            item.SetPhotosRootFolder(PhotosRootFolder);
                            SubscribeItem(item);
                            Items.Add(item);
                        }
                        ApplyFilter();
                        ItemsView.Refresh();
                    });
                }
                catch { }
            });
        }

        private void SaveData()
        {
            try
            {
                JsonDataService.Save(_jsonPath, Items.ToList());
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось сохранить чек-лист: {ex.Message}").ShowDialog();
            }
        }
    }
}
