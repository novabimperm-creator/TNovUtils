using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TNovCommon;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Опрос JSON-файла на сервере для окон чек-листа (checklist / autocheck / BIM проверки).
    /// Раз в <see cref="Interval"/> вне UI-потока сравнивается только отметка файла
    /// (LastWriteTimeUtc + длина) — один запрос метаданных по SMB. Файл читается и
    /// разбирается в фоне только если отметка изменилась, результат передаётся в UI через Dispatcher.
    /// Тик пропускается, если предыдущий ещё не закончился.
    /// </summary>
    internal sealed class ServerFilePoller<T> : IDisposable
    {
        /// <summary>Интервал опроса сервера (единый для всех окон чек-листа).</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

        private readonly string _path;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly Func<bool> _canPoll;
        private readonly Func<T> _load;
        private readonly Func<T, bool> _apply;
        private readonly string _logName;
        private readonly object _stampLock = new object();
        private FileStamp? _lastStamp;
        private int _running;
        private int _ownWriteGen;
        private bool _disposed;

        /// <param name="canPoll">UI-поток: можно ли сейчас опрашивать (не идёт редактирование/прогон).</param>
        /// <param name="load">Фоновый поток: чтение и десериализация файла. Без RevitAPI и WPF.</param>
        /// <param name="apply">UI-поток: применить прочитанное. false — не применено (занято), повторить на следующем тике.</param>
        public ServerFilePoller(string path, Dispatcher dispatcher, Func<bool> canPoll, Func<T> load, Func<T, bool> apply, string logName)
        {
            _path = path;
            _dispatcher = dispatcher;
            _canPoll = canPoll;
            _load = load;
            _apply = apply;
            _logName = logName;

            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Interval };
            _timer.Tick += (s, e) => CheckNow();
        }

        public void Start()
        {
            if (!string.IsNullOrEmpty(_path)) _timer.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }

        /// <summary>
        /// Внеочередная проверка (ручное обновление, активация окна). Вызывать с UI-потока.
        /// Как и тик таймера, не блокирует UI: сеть трогается только в фоне.
        /// </summary>
        public void CheckNow()
        {
            if (_disposed || string.IsNullOrEmpty(_path)) return;
            if (!_canPoll()) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return; // предыдущий тик ещё идёт

            int gen = Volatile.Read(ref _ownWriteGen);
            Task.Run(() =>
            {
                bool handedToUi = false;
                try
                {
                    FileStamp stamp = FileStamp.Of(_path);
                    lock (_stampLock)
                    {
                        if (_lastStamp.HasValue && _lastStamp.Value.Equals(stamp)) return;
                    }

                    T data = _load();

                    handedToUi = true;
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_disposed) return;
                            // Пока читали, окно само записало файл — прочитанное устарело.
                            if (gen != Volatile.Read(ref _ownWriteGen)) return;
                            if (!_apply(data)) return;
                            lock (_stampLock) _lastStamp = stamp;
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
                catch (Exception ex)
                {
                    // Сеть/файл занят/битый JSON — отметку не запоминаем, повторим на следующем тике.
                    Logger.Log($"Ошибка опроса {_logName}: " + ex.Message, 2);
                }
                finally
                {
                    if (!handedToUi) Interlocked.Exchange(ref _running, 0);
                }
            });
        }

        /// <summary>
        /// Вызывать сразу после успешной записи файла этим окном: запоминаем новую отметку,
        /// чтобы опрос не перечитывал собственную запись.
        /// </summary>
        public void MarkOwnWrite()
        {
            if (string.IsNullOrEmpty(_path)) return;
            Interlocked.Increment(ref _ownWriteGen);
            try
            {
                FileStamp stamp = FileStamp.Of(_path);
                lock (_stampLock) _lastStamp = stamp;
            }
            catch (Exception)
            {
                lock (_stampLock) _lastStamp = null; // не смогли прочитать отметку — перечитаем на тике
            }
        }
    }

    /// <summary>Отметка файла: время изменения + длина. Отсутствующий файл — отдельное состояние.</summary>
    internal struct FileStamp : IEquatable<FileStamp>
    {
        public DateTime WriteUtc;
        public long Length;

        public static FileStamp Of(string path)
        {
            var fi = new FileInfo(path); // один запрос атрибутов по SMB
            return fi.Exists
                ? new FileStamp { WriteUtc = fi.LastWriteTimeUtc, Length = fi.Length }
                : new FileStamp { WriteUtc = DateTime.MinValue, Length = -1 };
        }

        public bool Equals(FileStamp other) => WriteUtc == other.WriteUtc && Length == other.Length;
        public override bool Equals(object obj) => obj is FileStamp s && Equals(s);
        public override int GetHashCode() => WriteUtc.GetHashCode() ^ Length.GetHashCode();
    }
}
