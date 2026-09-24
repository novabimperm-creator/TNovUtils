using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json;
using TNovCommon;
using TNovCommon.Storage;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Опрос общего документа модели для окон чек-листа (checklist / autocheck / BIM проверки)
    /// через <see cref="IDocumentStore.PollAsync"/>: в файловом режиме — одна отметка файла по SMB,
    /// в API — условный GET (304 без тела). Всё вне UI-потока; документ разбирается в фоне,
    /// применяется через Dispatcher. Тик пропускается, если предыдущий ещё не закончился;
    /// результат, прочитанный до собственной записи окна, отбрасывается.
    /// </summary>
    internal sealed class DocumentPoller<T> : IDisposable
    {
        private readonly IDocumentStore _store;
        private readonly string _kind;
        private readonly string _key;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly Func<bool> _canPoll;
        private readonly Func<string, T> _parse;
        private readonly Func<StoredDocument, T, bool> _apply;
        private readonly Action<bool> _reachability;
        private readonly string _logName;
        private long _knownVersion;
        private int _running;
        private int _ownWriteGen;
        private bool _disposed;

        /// <param name="canPoll">UI-поток: можно ли сейчас опрашивать (не идёт редактирование/прогон/сохранение).</param>
        /// <param name="parse">Фоновый поток: разбор JSON (null — документа нет). Без RevitAPI и WPF.</param>
        /// <param name="apply">UI-поток: применить документ. false — не применено (занято), повторить на следующем тике.</param>
        /// <param name="reachability">UI-поток: true — сервер ответил, false — недоступен.</param>
        public DocumentPoller(IDocumentStore store, string kind, string key, TimeSpan interval, Dispatcher dispatcher,
            Func<bool> canPoll, Func<string, T> parse, Func<StoredDocument, T, bool> apply, Action<bool> reachability, string logName)
        {
            _store = store;
            _kind = kind;
            _key = key;
            _dispatcher = dispatcher;
            _canPoll = canPoll;
            _parse = parse;
            _apply = apply;
            _reachability = reachability;
            _logName = logName;

            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
            _timer.Tick += (s, e) => CheckNow();
        }

        public long KnownVersion => Interlocked.Read(ref _knownVersion);

        public void Start()
        {
            if (!string.IsNullOrEmpty(_key) && !_disposed) _timer.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }

        /// <summary>Версия, которую окно уже показывает (после загрузки или применения).</summary>
        public void SetKnown(long version) => Interlocked.Exchange(ref _knownVersion, version);

        /// <summary>
        /// Сразу после успешной записи этим окном: запоминаем новую версию, чтобы не перечитывать
        /// свою же запись, и помечаем уже начатые опросы как устаревшие.
        /// </summary>
        public void MarkOwnWrite(long version)
        {
            Interlocked.Increment(ref _ownWriteGen);
            SetKnown(version);
        }

        /// <summary>
        /// Внеочередная проверка (активация окна). Вызывать с UI-потока.
        /// Как и тик таймера, не блокирует UI: сеть трогается только в фоне.
        /// </summary>
        public void CheckNow()
        {
            if (_disposed || string.IsNullOrEmpty(_key)) return;
            if (!_canPoll()) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return; // предыдущий тик ещё идёт

            int gen = Volatile.Read(ref _ownWriteGen);
            long known = KnownVersion;
            Task.Run(async () =>
            {
                bool handedToUi = false;
                try
                {
                    StoredDocument doc = await _store.PollAsync(_kind, _key, known).ConfigureAwait(false);
                    T data = doc == null ? default(T) : _parse(doc.Json);

                    handedToUi = true;
                    _ = _dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_disposed) return;
                            _reachability(true);
                            if (doc == null) return;
                            // Пока читали, окно само записало документ — прочитанное устарело.
                            if (gen != Volatile.Read(ref _ownWriteGen)) return;
                            if (!_apply(doc, data)) return;
                            SetKnown(doc.Version);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Ошибка применения {_logName}: " + ex.Message, 2);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _running, 0);
                        }
                    }));
                }
                catch (JsonException ex)
                {
                    // Битый JSON — версию не запоминаем, повторим на следующем тике.
                    Logger.Log($"Ошибка разбора {_logName}: " + ex.Message, 2);
                }
                catch (Exception ex)
                {
                    // Сеть/API/шара недоступны — окно переходит в «только просмотр», опрос продолжается.
                    Logger.Log($"Ошибка опроса {_logName}: " + ex.Message, 2);
                    handedToUi = true;
                    _ = _dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!_disposed) _reachability(false);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _running, 0);
                        }
                    }));
                }
                finally
                {
                    if (!handedToUi) Interlocked.Exchange(ref _running, 0);
                }
            });
        }
    }
}
