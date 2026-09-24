using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using TNovCommon;
using TNovCommon.Storage;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    /// <summary>
    /// Чек-лист проектировщика: {docName},checklist.json (или документ checklist в TNovApi).
    /// Каждая правка — операция по id пункта над свежей копией (<see cref="SharedDocument{T}"/>).
    /// </summary>
    public sealed class DesignerChecklistViewModel : ObservableObject, IDisposable
    {
        private static readonly JsonSerializerSettings SaveSettings = new JsonSerializerSettings { Formatting = Formatting.Indented };

        private readonly ChecklistSession _session;
        private readonly ChecklistAttachments _attachments;
        private readonly Dispatcher _dispatcher;
        private readonly SharedDocument<List<CheckItem>> _doc;
        private readonly HashSet<string> _downloading = new HashSet<string>();
        private bool _isEditing;
        private bool _applying;
        private bool _disposed;

        public ObservableCollection<CheckItem> Items { get; } = new ObservableCollection<CheckItem>();
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

        /// <summary>Правки разрешены: данные загружены и сервер доступен.</summary>
        public bool CanEdit => _doc.CanEdit;

        public RelayCommand2 AddCommand { get; }
        public RelayCommand2 RemoveCommand { get; }
        public RelayCommand2 EditTitleCommand { get; }
        public RelayCommand2 PastePhotoCommand { get; }
        public RelayCommand2 DeletePhotoCommand { get; }
        public RelayCommand2 ViewPhotoCommand { get; }

        /// <summary>Шара ({docName},checklist_photos) или локальный кэш фото из API. Только путь, без сети.</summary>
        private string PhotosRootFolder => _attachments.PhotosRoot;

        public DesignerChecklistViewModel(ChecklistSession session)
        {
            _session = session;
            _attachments = session.Attachments;
            _dispatcher = Dispatcher.CurrentDispatcher;

            ItemsView = CollectionViewSource.GetDefaultView(Items);
            ItemsView.SortDescriptions.Add(new SortDescription(nameof(CheckItem.IsChecked), ListSortDirection.Ascending));
            ItemsView.SortDescriptions.Add(new SortDescription(nameof(CheckItem.CreationDate), ListSortDirection.Descending));
            ApplyFilter();

            Items.CollectionChanged += (s, e) => UpdateCreators();
            UpdateCreators();

            AddCommand = new RelayCommand2(_ => AddItem(), _ => CanEdit);
            RemoveCommand = new RelayCommand2(obj => RemoveItem(obj as CheckItem), _ => CanEdit);
            EditTitleCommand = new RelayCommand2(obj => EditTitle(obj as CheckItem), _ => CanEdit);
            PastePhotoCommand = new RelayCommand2(obj => PastePhoto(obj as CheckItem), _ => CanEdit);
            DeletePhotoCommand = new RelayCommand2(obj => DeletePhoto(obj as CheckItem), _ => CanEdit);
            ViewPhotoCommand = new RelayCommand2(obj => ViewPhoto(obj as CheckItem));

            // Окно открывается сразу, пункты приходят после фоновой загрузки.
            _doc = new SharedDocument<List<CheckItem>>(
                session, DocumentKinds.Checklist, "чек-лист", _dispatcher,
                Parse, Serialize, ApplyServer, () => _isEditing || _disposed);
            _doc.CanEditChanged += (s, e) => OnCanEditChanged();
            _doc.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Dispose();
        }

        /// <summary>Внеочередная проверка сервера (без блокировки UI).</summary>
        public void CheckServerNow() => _session.CheckNow();

        private void OnCanEditChanged()
        {
            OnPropertyChanged(nameof(CanEdit));
            AddCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
            EditTitleCommand.RaiseCanExecuteChanged();
            PastePhotoCommand.RaiseCanExecuteChanged();
            DeletePhotoCommand.RaiseCanExecuteChanged();
        }

        private static string UserName => RevitAPI.UiApplication?.Application?.Username ?? "";

        private void AddItem()
        {
            if (string.IsNullOrWhiteSpace(NewTaskText) || !CanEdit) return;

            var newItem = new CheckItem
            {
                Title = NewTaskText,
                IsChecked = false,
                Creator = UserName,
                CreationDate = DateTime.Now
            };
            AddLocal(newItem);
            NewTaskText = string.Empty;

            Guid id = newItem.Id;
            string title = newItem.Title, creator = newItem.Creator;
            DateTime created = newItem.CreationDate;
            _doc.Edit(items =>
            {
                if (items.Any(i => i.Id == id)) return false;
                items.Add(new CheckItem { Id = id, Title = title, IsChecked = false, Creator = creator, CreationDate = created });
                return true;
            });
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
            _isEditing = true;
            try
            {
                if (qwpfview.ShowDialog() != true) return;
            }
            finally
            {
                _isEditing = false;
            }
            if (!CanEdit) return;

            RemoveLocal(item);
            ItemsView.Refresh();

            Guid id = item.Id;
            string photoFolder = string.IsNullOrEmpty(PhotosRootFolder) ? null : Path.Combine(PhotosRootFolder, id.ToString());
            string removedFileId = null;
            var attachments = _attachments;
            _doc.Edit(
                items =>
                {
                    var target = items.FirstOrDefault(i => i.Id == id);
                    if (target == null) return false;
                    removedFileId = target.PhotoFileId;
                    items.Remove(target);
                    return true;
                },
                after: async () =>
                {
                    // Фото удалённого пункта — после сохранения документа (шара — тоже в фоне).
                    if (photoFolder != null && Directory.Exists(photoFolder))
                    {
                        try { Directory.Delete(photoFolder, true); }
                        catch { }
                    }
                    if (!string.IsNullOrEmpty(removedFileId))
                        await attachments.DeletePhotoAsync(removedFileId).ConfigureAwait(false);
                });
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
            _isEditing = true;
            try
            {
                if (window.ShowDialog() != true) return;
            }
            finally
            {
                _isEditing = false;
            }

            string newTitle = viewModel.ids;
            if (string.IsNullOrWhiteSpace(newTitle) || newTitle == item.Title || !CanEdit) return;

            SetLocal(() => item.Title = newTitle);
            Guid id = item.Id;
            _doc.Edit(items =>
            {
                var target = items.FirstOrDefault(i => i.Id == id);
                if (target == null || target.Title == newTitle) return false;
                target.Title = newTitle;
                return true;
            });
        }

        private void PastePhoto(CheckItem item)
        {
            if (item == null) return;
            if (string.IsNullOrEmpty(PhotosRootFolder))
            {
                new InfoWindow280("Не задана корневая папка для фотографий.").ShowDialog();
                return;
            }

            byte[] png;
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
                    // Буфер обмена — только в UI-потоке; кодируем в память, на диск/сервер — в фоне.
                    using (var ms = new MemoryStream())
                    {
                        bitmap.Save(ms, ImageFormat.Png);
                        png = ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                new InfoWindow280($"Не удалось вставить фото: {ex.Message}").ShowDialog();
                return;
            }

            Guid id = item.Id;
            string itemFolder = Path.Combine(PhotosRootFolder, id.ToString());
            string newFileName = $"{Guid.NewGuid()}.png";
            string newPath = Path.Combine(itemFolder, newFileName);
            string newFileId = null;
            string oldFileName = null, oldFileId = null;
            bool applied = false;
            var attachments = _attachments;

            _doc.Edit(
                items =>
                {
                    var target = items.FirstOrDefault(i => i.Id == id);
                    applied = target != null;
                    if (target == null) return false; // пункт успели удалить
                    oldFileName = target.PhotoFileName;
                    oldFileId = target.PhotoFileId;
                    target.PhotoFileName = newFileName;
                    target.PhotoFileId = newFileId;
                    return true;
                },
                before: async () =>
                {
                    Directory.CreateDirectory(itemFolder);
                    File.WriteAllBytes(newPath, png);
                    newFileId = await attachments.UploadPhotoAsync(id, newPath).ConfigureAwait(false);
                },
                after: async () =>
                {
                    if (!applied)
                    {
                        TryDeleteFile(newPath);
                        if (!string.IsNullOrEmpty(newFileId)) await attachments.DeletePhotoAsync(newFileId).ConfigureAwait(false);
                        return;
                    }
                    if (!string.IsNullOrEmpty(oldFileName) && oldFileName != newFileName)
                        TryDeleteFile(Path.Combine(itemFolder, oldFileName));
                    if (!string.IsNullOrEmpty(oldFileId) && oldFileId != newFileId)
                        await attachments.DeletePhotoAsync(oldFileId).ConfigureAwait(false);
                });
        }

        private void DeletePhoto(CheckItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.PhotoFileName) || !CanEdit) return;

            Guid id = item.Id;
            string itemFolder = string.IsNullOrEmpty(PhotosRootFolder) ? null : Path.Combine(PhotosRootFolder, id.ToString());
            string oldFileName = null, oldFileId = null;
            var attachments = _attachments;

            SetLocal(() => item.PhotoFileName = null);
            _doc.Edit(
                items =>
                {
                    var target = items.FirstOrDefault(i => i.Id == id);
                    if (target == null || string.IsNullOrEmpty(target.PhotoFileName)) return false;
                    oldFileName = target.PhotoFileName;
                    oldFileId = target.PhotoFileId;
                    target.PhotoFileName = null;
                    target.PhotoFileId = null;
                    return true;
                },
                after: async () =>
                {
                    if (itemFolder != null && !string.IsNullOrEmpty(oldFileName))
                        TryDeleteFile(Path.Combine(itemFolder, oldFileName));
                    if (!string.IsNullOrEmpty(oldFileId))
                        await attachments.DeletePhotoAsync(oldFileId).ConfigureAwait(false);
                });
        }

        private void ViewPhoto(CheckItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.PhotoFullPath))
                return;
            string path = item.PhotoFullPath;
            // File.Exists по шаре — тоже сеть: проверяем в фоне.
            Task.Run(() =>
            {
                if (!File.Exists(path)) return;
                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var proc = new System.Diagnostics.Process();
                        proc.StartInfo.FileName = path;
                        proc.StartInfo.UseShellExecute = true;
                        proc.Start();
                    }
                    catch (Exception ex)
                    {
                        new InfoWindow280($"Не удалось открыть фото: {ex.Message}").ShowDialog();
                    }
                }));
            });
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_applying || _disposed) return;
            if (e.PropertyName != nameof(CheckItem.IsChecked)) return; // остальное меняется только командами

            var item = (CheckItem)sender;
            Guid id = item.Id;
            bool isChecked = item.IsChecked;
            bool queued = _doc.Edit(items =>
            {
                var target = items.FirstOrDefault(i => i.Id == id);
                if (target == null || target.IsChecked == isChecked) return false;
                target.IsChecked = isChecked;
                return true;
            });
            if (!queued) SetLocal(() => item.IsChecked = !isChecked); // правка невозможна — вернуть отметку
        }

        // ---------- локальный список ----------

        private void SetLocal(Action change)
        {
            _applying = true;
            try { change(); }
            finally { _applying = false; }
        }

        private void AddLocal(CheckItem item)
        {
            item.SetPhotosRootFolder(PhotosRootFolder);
            item.PropertyChanged += Item_PropertyChanged;
            Items.Add(item);
            ItemsView.Refresh();
        }

        private void RemoveLocal(CheckItem item)
        {
            item.PropertyChanged -= Item_PropertyChanged;
            Items.Remove(item);
        }

        /// <summary>
        /// UI-поток: показать документ с сервера (загрузка, опрос, итог сохранения) — на месте,
        /// по id пункта, чтобы список не перестраивался и не прыгала прокрутка.
        /// </summary>
        private void ApplyServer(StoredDocument stored, List<CheckItem> server)
        {
            bool changed = false;
            var byId = server.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());

            _applying = true;
            try
            {
                foreach (var local in Items.ToList())
                {
                    if (!byId.ContainsKey(local.Id))
                    {
                        RemoveLocal(local);
                        changed = true;
                    }
                }

                foreach (var remote in byId.Values)
                {
                    var local = Items.FirstOrDefault(i => i.Id == remote.Id);
                    if (local == null)
                    {
                        AddLocal(remote);
                        changed = true;
                        continue;
                    }
                    if (local.Title != remote.Title) { local.Title = remote.Title; changed = true; }
                    if (local.IsChecked != remote.IsChecked) { local.IsChecked = remote.IsChecked; changed = true; }
                    if (local.CreationDate != remote.CreationDate) { local.CreationDate = remote.CreationDate; changed = true; }
                    if (local.Creator != remote.Creator) { local.Creator = remote.Creator; changed = true; }
                    local.PhotoFileId = remote.PhotoFileId;
                    if (local.PhotoFileName != remote.PhotoFileName) { local.PhotoFileName = remote.PhotoFileName; changed = true; }
                }
            }
            finally
            {
                _applying = false;
            }

            if (changed)
            {
                UpdateCreators();
                ItemsView.Refresh();
            }
            EnsurePhotos();
        }

        /// <summary>Режим API: докачать в локальный кэш фото, снятые на других машинах.</summary>
        private void EnsurePhotos()
        {
            if (!_attachments.UsesApi) return;
            var missing = Items
                .Where(i => !string.IsNullOrEmpty(i.PhotoFileId) && !string.IsNullOrEmpty(i.PhotoFileName)
                            && _downloading.Add(i.PhotoFileId))
                .Select(i => (Item: i, i.Id, i.PhotoFileName, i.PhotoFileId))
                .ToList();
            if (missing.Count == 0) return;

            var attachments = _attachments;
            Task.Run(async () =>
            {
                foreach (var m in missing)
                {
                    bool downloaded = false;
                    try { downloaded = await attachments.EnsurePhotoAsync(m.Id, m.PhotoFileName, m.PhotoFileId).ConfigureAwait(false); }
                    catch (Exception ex) { Logger.Log("Не удалось скачать фото пункта чек-листа: " + ex.Message, 2); }

                    _ = _dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!downloaded) _downloading.Remove(m.PhotoFileId); // повторим при следующем обновлении
                        if (downloaded && !_disposed && m.Item.PhotoFileId == m.PhotoFileId) m.Item.RefreshPhoto();
                    }));
                }
            });
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

            if (Creators.Count > 0 && unique.SequenceEqual(Creators.Skip(1))) return;

            string selected = SelectedCreator;
            Creators.Clear();
            Creators.Add("Все");
            foreach (var creator in unique)
                Creators.Add(creator);

            SelectedCreator = !string.IsNullOrEmpty(selected) && Creators.Contains(selected) ? selected : "Все";
        }

        // ---------- формат документа ----------

        /// <summary>Фон: разбор документа (тот же формат, что у прежнего JsonDataService.Load).</summary>
        private static List<CheckItem> Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return new List<CheckItem>();
            var items = JsonConvert.DeserializeObject<List<CheckItem>>(json) ?? new List<CheckItem>();
            // Конструктор CheckItem выдаёт случайный id — у пункта без "id" в JSON он менялся бы при каждом чтении.
            var raw = JArray.Parse(json);
            for (int i = 0; i < items.Count; i++)
            {
                // Старые пункты без id: id выводится из содержимого, чтобы окно и операция
                // сохранения нашли один и тот же пункт; при первом сохранении он запишется.
                bool hasId = i < raw.Count && raw[i] is JObject o && o["id"] != null && o["id"].Type != JTokenType.Null;
                if (!hasId || items[i].Id == Guid.Empty) items[i].Id = StableId(i, items[i]);
            }
            return items;
        }

        private static string Serialize(List<CheckItem> items) => JsonConvert.SerializeObject(items, SaveSettings);

        private static Guid StableId(int index, CheckItem item)
        {
            using (var md5 = MD5.Create())
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(
                    index + "|" + item.Title + "|" + item.CreationDate.Ticks + "|" + item.Creator)));
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
