using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TNovCommon;
using TNovCommon.Storage;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Общий документ модели в окне чек-листа (checklist / autocheck / BIM проверки).
    ///
    /// Загрузка, опрос и сохранение — вне UI-потока. Каждая правка — операция над свежей
    /// копией с сервера (<see cref="DocumentStoreExtensions.UpdateAsync"/>): при конфликте
    /// версий та же операция применяется к новой копии, поэтому одновременные правки разных
    /// людей по разным пунктам сохраняются. Правки окна выполняются по очереди; после
    /// последней окно показывает то, что реально лежит на сервере (с чужими изменениями).
    ///
    /// Сервер недоступен (данные из кэша, ошибка сохранения или опроса) — «только просмотр»:
    /// <see cref="CanEdit"/> = false, опрос продолжается; первый удачный опрос возвращает правку
    /// и перечитывает документ.
    /// </summary>
    internal sealed class SharedDocument<T> : IDisposable where T : class
    {
        private readonly ChecklistSession _session;
        private readonly string _kind;
        private readonly string _title;
        private readonly Dispatcher _dispatcher;
        private readonly Func<string, T> _parse;
        private readonly Func<T, string> _serialize;
        private readonly Action<StoredDocument, T> _apply;
        private readonly Func<bool> _isBusy;
        private readonly DocumentPoller<T> _poller;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();

        private StoredDocument _current;
        private int _pending;      // UI-поток: правок в очереди на сохранение
        private bool _loaded;
        private bool _loading;
        private bool _offline;
        private bool _disposed;

        /// <summary>Правки разрешены (или модель не сохранена — тогда правки только в окне).</summary>
        public event EventHandler CanEditChanged;

        /// <param name="title">Для сообщений: «чек-лист», «автопроверки», «BIM-проверки».</param>
        /// <param name="parse">Фон: JSON (null — документа нет) → модель. Без RevitAPI и WPF.</param>
        /// <param name="serialize">Фон: модель → JSON (Formatting.Indented, как в файлах).</param>
        /// <param name="apply">UI-поток: показать документ с сервера.</param>
        /// <param name="isBusy">UI-поток: окно занято (диалог, прогон) — опрос не применяем.</param>
        public SharedDocument(ChecklistSession session, string kind, string title, Dispatcher dispatcher,
            Func<string, T> parse, Func<T, string> serialize, Action<StoredDocument, T> apply, Func<bool> isBusy)
        {
            _session = session;
            _kind = kind;
            _title = title;
            _dispatcher = dispatcher;
            _parse = parse;
            _serialize = serialize;
            _apply = apply;
            _isBusy = isBusy;

            _poller = new DocumentPoller<T>(session.Store, kind, session.ModelKey, session.PollInterval, dispatcher,
                () => !_disposed && _pending == 0 && !_loading && !_isBusy(),
                parse, ApplyPolled, OnReachability, kind);
            _session.CheckNowRequested += Session_CheckNowRequested;
        }

        private bool HasKey => !string.IsNullOrEmpty(_session.ModelKey);

        public bool CanEdit => !HasKey || (_loaded && !_offline);

        public bool HasPendingSaves => _pending > 0;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.CheckNowRequested -= Session_CheckNowRequested;
            _poller.Dispose();
            _session.SetState(_kind, false, false);
        }

        private void Session_CheckNowRequested(object sender, EventArgs e) => _poller.CheckNow();

        /// <summary>UI-поток: первая загрузка (окно уже показано, данные придут позже) и запуск опроса.</summary>
        public void Start()
        {
            if (!HasKey)
            {
                SetState(loaded: true, offline: false);
                _apply(StoredDocument.Missing(_kind, null), _parse(null));
                return;
            }
            Reload(startPolling: true);
        }

        /// <summary>UI-поток: перечитать документ в фоне и показать.</summary>
        public void Reload(bool startPolling = false)
        {
            if (_disposed || !HasKey || _loading) return;
            _loading = true;
            UpdateSessionState();

            Task.Run(async () =>
            {
                StoredDocument doc = null;
                T data = null;
                Exception error = null;
                try
                {
                    doc = await _session.Store.LoadAsync(_kind, _session.ModelKey).ConfigureAwait(false);
                    data = _parse(doc.Json);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    _loading = false;
                    if (_disposed) return;
                    try
                    {
                        if (error != null)
                        {
                            Logger.Log($"Не удалось загрузить {_title}: " + error.Message, 4);
                            // Без данных — «только просмотр» до первого удачного опроса.
                            SetState(_loaded, offline: true);
                            return;
                        }

                        lock (_sync) _current = doc;
                        _poller.SetKnown(doc.Version);
                        SetState(loaded: true, offline: doc.FromCache);
                        // Пока грузили, окно успело поставить правки в очередь — покажет их сохранение.
                        if (_pending == 0) _apply(doc, data);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Ошибка применения {_title}: " + ex, 4);
                    }
                    finally
                    {
                        UpdateSessionState();
                        if (startPolling) _poller.Start();
                    }
                }));
            });
        }

        /// <summary>
        /// UI-поток: поставить правку в очередь. Вызывающий уже поменял то, что видно в окне;
        /// здесь та же правка применяется к копии с сервера.
        /// </summary>
        /// <param name="mutate">Фон: изменить модель (свежую копию); false — менять нечего. Может вызываться повторно.</param>
        /// <param name="before">Фон: до сохранения документа (запись лога, выгрузка фото). Ошибка отменяет правку.</param>
        /// <param name="after">Фон: после удачного сохранения (удаление старых файлов). Ошибки только в лог.</param>
        /// <returns>false — правка сейчас невозможна (сервер недоступен, данные ещё грузятся).</returns>
        public bool Edit(Func<T, bool> mutate, Func<Task> before = null, Func<Task> after = null)
        {
            if (_disposed) return false;
            if (!HasKey) return true; // модель не сохранена — как раньше, правки живут только в окне
            if (!CanEdit) return false;

            _pending++;
            Task.Run(async () =>
            {
                StoredDocument saved = null;
                T data = null;
                Exception error = null;

                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (before != null) await before().ConfigureAwait(false);

                    StoredDocument known;
                    lock (_sync) known = _current;
                    saved = await _session.Store.UpdateAsync(_kind, _session.ModelKey, json =>
                    {
                        T model = _parse(json);
                        return mutate(model) ? _serialize(model) : null;
                    }, known).ConfigureAwait(false);
                    lock (_sync) _current = saved;
                    data = _parse(saved.Json);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    _gate.Release();
                }

                if (error == null && after != null)
                {
                    try { await after().ConfigureAwait(false); }
                    catch (Exception ex) { Logger.Log($"{_title}: не удалось убрать старые файлы: " + ex.Message, 2); }
                }

                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    _pending--;
                    if (_disposed) return;
                    if (error != null)
                        OnSaveFailed(error);
                    else
                        OnSaved(saved, data);
                }));
            });
            return true;
        }

        private void OnSaved(StoredDocument saved, T data)
        {
            _poller.MarkOwnWrite(saved.Version);
            if (_pending > 0) return; // следующая правка из очереди покажет итог
            try { _apply(saved, data); }
            catch (Exception ex) { Logger.Log($"Ошибка применения {_title}: " + ex, 4); }
        }

        private void OnSaveFailed(Exception error)
        {
            Logger.Log($"Не удалось сохранить {_title}: " + error, 4);
            bool unavailable = error is DocumentStoreUnavailableException || error is IOException
                || error is UnauthorizedAccessException;
            if (unavailable) SetState(_loaded, offline: true);

            string text = unavailable
                ? $"Не удалось сохранить {_title}: сервер TNov недоступен.\nИзменение не записано. Окно переходит в режим «только просмотр»."
                : $"Не удалось сохранить {_title}: {error.Message}\nИзменение не записано.";
            new InfoWindow280(text).ShowDialog();

            // Вернуть окну то, что реально лежит на сервере (или в кэше).
            Reload();
        }

        /// <returns>false — окно занято, повторить на следующем тике.</returns>
        private bool ApplyPolled(StoredDocument doc, T data)
        {
            if (_disposed || _pending > 0 || _loading || _isBusy()) return false;
            lock (_sync) _current = doc;
            _apply(doc, data);
            return true;
        }

        private void OnReachability(bool reachable)
        {
            if (reachable)
            {
                // Связь вернулась — перечитать (в кэше могла лежать старая версия); правка
                // вернётся, когда загрузка подтвердит, что данные с сервера, а не из кэша.
                if (_offline || !_loaded) Reload();
                return;
            }
            SetState(_loaded, offline: true);
        }

        private void SetState(bool loaded, bool offline)
        {
            bool couldEdit = CanEdit;
            _loaded = loaded;
            _offline = offline;
            UpdateSessionState();
            if (couldEdit != CanEdit) CanEditChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateSessionState() => _session.SetState(_kind, _loading || (!_loaded && !_offline), _offline);
    }
}
