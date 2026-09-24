using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovCommon.Storage;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Общее для всех разделов окна чек-листа: хранилище (файлы или TNovApi), ключ модели,
    /// вложения (фото, логи) и состояние связи для плашки «только просмотр».
    /// Создаётся в UI-потоке Revit (ключ модели читается из Document); сети не трогает.
    /// </summary>
    public sealed class ChecklistSession : ObservableObject
    {
        public const string OfflineText = "Сервер TNov недоступен — только просмотр";
        public const string LoadingText = "Загрузка…";

        private readonly HashSet<string> _offline = new HashSet<string>();
        private readonly HashSet<string> _loading = new HashSet<string>();

        public IDocumentStore Store { get; }

        /// <summary>Ключ модели; null — модель не сохранена, правки живут только в окне (как раньше без файла).</summary>
        public string ModelKey { get; }

        public ChecklistAttachments Attachments { get; }

        /// <summary>Интервал опроса: API дешёвый (304 без тела) — чаще, SMB — как раньше, раз в 20 с.</summary>
        public TimeSpan PollInterval =>
            Store is IApiDocumentStore ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(20);

        /// <summary>Внеочередная проверка сервера всеми разделами (активация окна).</summary>
        public event EventHandler CheckNowRequested;

        public ChecklistSession(IDocumentStore store, string modelKey)
        {
            Store = store;
            ModelKey = modelKey;
            Attachments = new ChecklistAttachments(store, modelKey);
        }

        /// <summary>UI-поток Revit: хранилище по TNovConfig.json и ключ модели.</summary>
        public static ChecklistSession ForDocument(Document doc) =>
            new ChecklistSession(DocumentStores.ForChecklist(), DocumentKeys.ForDocument(doc));

        public void CheckNow() => CheckNowRequested?.Invoke(this, EventArgs.Empty);

        public bool IsReadOnly => _offline.Count > 0;

        public string StatusText => IsReadOnly ? OfflineText : _loading.Count > 0 ? LoadingText : "";

        public System.Windows.Visibility StatusVisibility => string.IsNullOrEmpty(StatusText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        public System.Windows.Visibility OfflineVisibility => IsReadOnly ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public System.Windows.Visibility LoadingVisibility => !IsReadOnly && _loading.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>UI-поток: состояние раздела (kind) — загружается / без связи.</summary>
        internal void SetState(string kind, bool loading, bool offline)
        {
            bool changed = loading ? _loading.Add(kind) : _loading.Remove(kind);
            changed |= offline ? _offline.Add(kind) : _offline.Remove(kind);
            if (!changed) return;

            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusVisibility));
            OnPropertyChanged(nameof(OfflineVisibility));
            OnPropertyChanged(nameof(LoadingVisibility));
        }
    }
}
