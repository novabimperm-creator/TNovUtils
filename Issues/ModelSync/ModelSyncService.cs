using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json;
using TNovCommon;
using TNovUtils.Issues.Api;
using TNovUtils.Issues.Config;
using TNovUtils.Issues.Revit;
using TNovUtils.Issues.UI;

namespace TNovUtils.Issues.ModelSync
{
    /// <summary>
    /// «Модель» TNovPRO: сайт обновляется по синхронизациям с центральной моделью
    /// (решение Виктора 2026-09-28, без ночных выгрузок).
    ///
    /// Правило отправки — «по последней синхронизации» (решение Виктора 2026-10-21):
    /// сайт получает состояние модели, когда после последней синхронизации прошёл
    /// ЧАС без новых. Поэтому работа разбита на два шага.
    ///
    ///  1. ВЫГРУЗКА при каждой синхронизации (в контексте Revit API): копим id
    ///     изменённых и удалённых (DocumentChanged — только множества чисел), после
    ///     синхронизации выгружаем ИХ (паспорт + геометрия) со сверочными цифрами
    ///     по категориям и кладём приращение в очередь НА ДИСК. Ровно в этот момент
    ///     локальная копия равна центральной — позже из документа читать нельзя:
    ///     утащим правки, которые человек не синхронизировал и отбросил.
    ///  2. ОТПРАВКА в фоне, без Revit API: когда после последней выгрузки прошёл час
    ///     без новых (<see cref="IdleDelay"/>), либо при закрытии документа (ждать час
    ///     после выхода из Revit некому), либо при следующем запуске Revit — если
    ///     очередь осталась с прошлого раза. Приращения уходят по порядку, сервер
    ///     применяет «новее побеждает»; итог на сайте — состояние последней
    ///     синхронизации. Не ушло (нет сети, истёк вход) — очередь ждёт следующей
    ///     попытки. Проекты, которых нет на сайте, не трогаем вовсе.
    ///
    /// Подключение — в TNov/Application.cs: DocumentChanged → <see cref="OnDocumentChanged"/>,
    /// DocumentSynchronizedWithCentral → <see cref="OnSynchronized"/>,
    /// DocumentSaved (файл без совместной работы) → <see cref="OnSaved"/>,
    /// DocumentClosing → <see cref="OnClosing"/>.
    /// Контракт с сайтом — doc/BIM-ASK-SYNC.md в репозитории TNovPRO.
    /// </summary>
    public static class ModelSyncService
    {
        // Больше — через загрузку частями: у сервера предел тела запроса 50 МБ.
        private const int DirectLimit = 45 * 1024 * 1024;
        private static readonly TimeSpan LoadedTtl = TimeSpan.FromMinutes(10);
        // Через сколько после последней синхронизации отправлять. Для проверки — TNOVPRO_SYNC_IDLE_MINUTES.
        private static readonly TimeSpan IdleDelay = ReadIdleDelay();
        // После неудачной отправки (нет сети) — не долбить сервер каждую минуту.
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ScanPeriod = TimeSpan.FromMinutes(1);

        private sealed class Pending
        {
            public HashSet<long> Changed = new HashSet<long>();
            public HashSet<long> Deleted = new HashSet<long>();
            // Самолечение (2026-10-08): категории, которые по сверке сайта
            // разошлись с моделью (ответ прошлой отправки). При следующей
            // выгрузке уходят ЦЕЛИКОМ, и сайт сносит у себя лишнее.
            public HashSet<string> Heal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool IsEmpty => Changed.Count == 0 && Deleted.Count == 0;
        }

        /// <summary>Что лежит в очереди документа (state.json рядом с приращениями).</summary>
        private sealed class QueueState
        {
            public string Document;
            public string RevitUser;
            public DateTime LastStagedUtc;
            public DateTime LastAttemptUtc;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Pending> PendingByDoc =
            new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Sending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Документы, загруженные на сайт. null — ещё не знаем (тогда отправляем
        // и узнаём по ответу: 404 значит «не ведётся»).
        private static HashSet<string> _loaded;
        private static DateTime _loadedAt = DateTime.MinValue;
        private static bool _refreshing;
        private static bool _scanning;

        private static ApiSession Session => IssuesHost.Session;

        /// <summary>
        /// Можно ли отправлять: только корпоративная конфигурация TNov. В личной (вне сети
        /// компании) модели на сайт не уходят, даже если человек входил в TNovPRO.
        /// </summary>
        private static bool CanSend => _corporate;

        private static string _syncKey;
        // Корпоративная установка TNov: из сети компании сайт принимает синхронизацию
        // без входа и без ключа (решение Виктора: «при входе в модель автоматически»).
        private static bool _corporate;

        private static TimeSpan ReadIdleDelay()
        {
            try
            {
                var v = Environment.GetEnvironmentVariable("TNOVPRO_SYNC_IDLE_MINUTES");
                if (!string.IsNullOrWhiteSpace(v) && double.TryParse(v, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var minutes) && minutes >= 0)
                    return TimeSpan.FromMinutes(minutes);
            }
            catch { }
            return TimeSpan.FromHours(1);
        }

        /// <summary>
        /// Вызывается из TNov при запуске Revit. Ключ синхронизации — файл
        /// «tnovpro\sync-key.txt» на корпоративной папке раздачи TNov (serverPath),
        /// а не в коде: репозитории плагина публичные. Для отладки — переменная
        /// TNOVPRO_SYNC_KEY. Читается в фоне: сетевая папка может отвечать секундами.
        /// Здесь же стартует фоновый обход очередей: отправка по часу тишины и
        /// досылка того, что осталось с прошлого запуска Revit.
        /// </summary>
        public static void Configure(string serverPath)
        {
            _corporate = !string.IsNullOrEmpty(serverPath);
            Task.Run(async () =>
            {
                try
                {
                    var key = Environment.GetEnvironmentVariable("TNOVPRO_SYNC_KEY");
                    if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrEmpty(serverPath))
                    {
                        var file = Path.Combine(serverPath, "tnovpro", "sync-key.txt");
                        if (File.Exists(file)) key = File.ReadAllText(file);
                    }
                    key = key?.Trim();
                    if (!string.IsNullOrEmpty(key))
                    {
                        _syncKey = key;
                        Session.Client.SyncKey = key;
                        PluginLog.Write("[ModelSync] ключ синхронизации найден — синхронизация без входа включена");
                    }
                }
                catch (Exception ex) { PluginLog.Write("[ModelSync] ключ синхронизации: " + ex.Message); }

                if (!CanSend) return;
                PluginLog.Write($"[ModelSync] отправка — через {IdleDelay.TotalMinutes:N0} мин после последней синхронизации");
                // Очереди прошлого запуска — не раньше чем через полминуты: Revit ещё поднимается.
                await Task.Delay(TimeSpan.FromSeconds(30));
                for (;;)
                {
                    try { await ScanQueuesAsync(); }
                    catch (Exception ex) { PluginLog.Write("[ModelSync] обход очередей: " + ex.Message); }
                    await Task.Delay(ScanPeriod);
                }
            });
        }

        // ── Учёт изменений ─────────────────────────────────────────────────

        public static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
                var name = ModelExporter.DocumentName(doc);
                if (IsDetached(name)) return;
                RefreshLoadedInBackground();
                if (IsKnownNotLoaded(name)) return;
                lock (Gate)
                {
                    var p = Get(name);
                    foreach (var id in e.GetAddedElementIds()) p.Changed.Add(id.LongValue());
                    foreach (var id in e.GetModifiedElementIds()) p.Changed.Add(id.LongValue());
                    foreach (var id in e.GetDeletedElementIds())
                    {
                        var v = id.LongValue();
                        p.Changed.Remove(v);
                        p.Deleted.Add(v);
                    }
                }
            }
            catch (Exception ex)
            {
                // Обработчик события Revit не должен ронять правку человека.
                PluginLog.Write("[ModelSync] учёт изменений: " + ex.Message);
            }
        }

        /// <summary>После «Синхронизировать с центральной моделью».</summary>
        public static void OnSynchronized(Document doc) => Stage(doc, "синхронизация");

        /// <summary>Файл без совместной работы: сохранение — то же, что синхронизация.</summary>
        public static void OnSaved(Document doc)
        {
            if (doc == null || doc.IsWorkshared) return;
            Stage(doc, "сохранение");
        }

        /// <summary>
        /// Документ закрывают: ждать час после выхода из Revit некому — очередь
        /// уходит сейчас (в фоне; не успеет до выхода — доедет при следующем запуске).
        /// Документ при этом не читаем: что не синхронизировано, того на сайте и не должно быть.
        /// </summary>
        public static void OnClosing(Document doc)
        {
            try
            {
                if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
                var name = ModelExporter.DocumentName(doc);
                if (IsDetached(name) || !CanSend) return;
                if (QueuedFiles(name).Length == 0) return;
                PluginLog.Write($"[ModelSync] «{name}»: документ закрывают — отправляю накопленное");
                Task.Run(() => SendQueuedAsync(name));
            }
            catch (Exception ex) { PluginLog.Write("[ModelSync] закрытие документа: " + ex.Message); }
        }

        /// <summary>
        /// Выгрузить накопленное в очередь. Вызывается в контексте Revit API
        /// (обработчик события): чтение модели — здесь, сеть — потом и в фоне.
        /// </summary>
        private static void Stage(Document doc, string why)
        {
            string name = null;
            try
            {
                if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
                name = ModelExporter.DocumentName(doc);
                if (IsDetached(name)) return;
                if (IsKnownNotLoaded(name)) { Drop(name); return; }
                if (!CanSend) { Drop(name); return; }

                Pending snap;
                lock (Gate)
                {
                    var p = Get(name);
                    Merge(p, LoadSaved(name));
                    if (p.IsEmpty && p.Heal.Count == 0)
                    {
                        // Нечего выгружать, но синхронизация была: сдвигаем «час тишины»,
                        // чтобы отправка шла по ПОСЛЕДНЕЙ синхронизации, а не по последней правке.
                        TouchState(name, doc);
                        return;
                    }
                    snap = new Pending
                    {
                        Changed = new HashSet<long>(p.Changed),
                        Deleted = new HashSet<long>(p.Deleted),
                        Heal = new HashSet<string>(p.Heal, StringComparer.OrdinalIgnoreCase),
                    };
                }

                var clock = Stopwatch.StartNew();
                var changed = Expand(doc, snap.Changed);
                // Самолечение: разъехавшиеся категории уходят целиком — сайт
                // дополнит недостающее и снесёт у себя то, чего в модели нет.
                if (snap.Heal.Count > 0) changed.UnionWith(CollectCategories(doc, snap.Heal));
                var counts = ModelExporter.CountCategories(doc);
                var glb = ModelExporter.ExportElements(doc, changed, snap.Deleted, counts, DateTime.UtcNow,
                                                       snap.Heal.Count > 0 ? snap.Heal : null);
                clock.Stop();

                var file = Enqueue(name, glb);
                lock (Gate)
                {
                    // Убираем только выгруженное: правки после этой синхронизации — в следующую.
                    var p = Get(name);
                    p.Changed.ExceptWith(snap.Changed);
                    p.Deleted.ExceptWith(snap.Deleted);
                    p.Heal.ExceptWith(snap.Heal);
                    DeleteSaved(name);
                    if (!p.IsEmpty || p.Heal.Count > 0) Save(name, p);
                }
                TouchState(name, doc);
                PluginLog.Write($"[ModelSync] {why} «{name}»: изменено {snap.Changed.Count}"
                                + (changed.Count != snap.Changed.Count ? $" (с экземплярами типов и лечением {changed.Count})" : "")
                                + (snap.Heal.Count > 0 ? $", лечение [{string.Join(", ", snap.Heal)}]" : "")
                                + $", удалено {snap.Deleted.Count}, {glb.Length / 1024} КБ, {clock.ElapsedMilliseconds} мс"
                                + $" — в очередь ({Path.GetFileName(file)}), на сайт через {IdleDelay.TotalMinutes:N0} мин после последней синхронизации");
            }
            catch (Exception ex)
            {
                PluginLog.Write("[ModelSync] выгрузка: " + ex);
                // Не выгрузилось — id остаются в накопленном и уйдут со следующей синхронизацией.
                if (name != null) lock (Gate) Save(name, Get(name));
            }
        }

        // ── Отправка очереди ─────────────────────────────────────────────────

        /// <summary>Фоновый обход: чьи очереди созрели (час тишины) — те отправляем.</summary>
        private static async Task ScanQueuesAsync()
        {
            lock (Gate)
            {
                if (_scanning) return;
                _scanning = true;
            }
            try
            {
                var root = QueueRoot();
                if (!Directory.Exists(root)) return;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    QueueState state;
                    try { state = LoadState(dir); } catch { continue; }
                    if (state == null || string.IsNullOrEmpty(state.Document)) continue;
                    if (QueuedFiles(state.Document).Length == 0) continue;
                    var now = DateTime.UtcNow;
                    if (now - state.LastStagedUtc < IdleDelay) continue;
                    if (now - state.LastAttemptUtc < RetryDelay) continue;
                    await SendQueuedAsync(state.Document);
                }
            }
            finally
            {
                lock (Gate) _scanning = false;
            }
        }

        /// <summary>Отправить все приращения документа по порядку. Без Revit API — только файлы и сеть.</summary>
        private static async Task SendQueuedAsync(string name)
        {
            lock (Gate)
            {
                if (Sending.Contains(name)) return;   // уже идёт
                Sending.Add(name);
            }
            try
            {
                var state = LoadState(QueueDir(name)) ?? new QueueState { Document = name };
                state.LastAttemptUtc = DateTime.UtcNow;
                SaveState(name, state);
                try { Session.Client.RevitUser = state.RevitUser; } catch { }

                var files = QueuedFiles(name);
                int sent = 0;
                ApiClient.SyncOutcome last = null;
                foreach (var file in files)
                {
                    ApiClient.SyncOutcome outcome;
                    var glb = File.ReadAllBytes(file);
                    if (glb.Length <= DirectLimit)
                    {
                        outcome = await Session.Client.SyncModelAsync(name, glb);
                    }
                    else
                    {
                        var uploadId = await Session.Client.UploadChunkedAsync(file);
                        outcome = await Session.Client.SyncModelAsync(name, uploadId);
                    }
                    if (!outcome.Loaded)
                    {
                        // Проект на сайт не загружен — очередь ему не нужна.
                        lock (Gate) MarkNotLoaded(name);
                        foreach (var f in files) TryDelete(f);
                        PluginLog.Write($"[ModelSync] «{name}»: проект не загружен на сайт — не синхронизируем");
                        return;
                    }
                    TryDelete(file);
                    sent++;
                    last = outcome;
                }
                if (last != null)
                {
                    lock (Gate)
                    {
                        // Что сверка нашла СЕЙЧАС — дошлём целиком при следующей синхронизации.
                        var p = Get(name);
                        p.Heal.UnionWith(last.DriftCategories);
                        if (!p.IsEmpty || p.Heal.Count > 0) Save(name, p);
                    }
                    PluginLog.Write(last.DriftCategories.Count > 0
                        ? $"[ModelSync] «{name}»: сайт обновлён ({sent} прир.); расходится [{string.Join(", ", last.DriftCategories)}] — дошлём целиком при следующей синхронизации"
                        : $"[ModelSync] «{name}»: сайт обновлён ({sent} прир.)");
                }
            }
            catch (Exception ex)
            {
                // Нет сети, истёк вход — не теряем: очередь на диске, попробуем позже.
                PluginLog.Write($"[ModelSync] «{name}»: не отправлено ({ex.Message}) — повторим через {RetryDelay.TotalMinutes:N0} мин");
            }
            finally
            {
                lock (Gate) Sending.Remove(name);
            }
        }

        /// <summary>
        /// Все элементы перечисленных категорий (по имени, как в сверке) — для
        /// самолечения: разъехавшаяся категория уходит на сайт целиком. Лишние
        /// id не страшны: отбор IsAskCandidate в выгрузке общий.
        /// </summary>
        private static HashSet<long> CollectCategories(Document doc, ICollection<string> cats)
        {
            var result = new HashSet<long>();
            var want = new HashSet<string>(cats, StringComparer.OrdinalIgnoreCase);
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var c = el.Category;
                if (c != null && want.Contains(c.Name ?? "")) result.Add(el.Id.LongValue());
            }
            return result;
        }

        /// <summary>
        /// Изменили ТИП или УРОВЕНЬ — Revit сообщает о нём самом, а не о его
        /// экземплярах. Добираем экземпляры одним проходом, иначе объёмы и
        /// «на каком этаже» на сайте остались бы старыми.
        /// </summary>
        private static HashSet<long> Expand(Document doc, HashSet<long> changed)
        {
            var result = new HashSet<long>(changed);
            var types = new HashSet<long>();
            var levels = new HashSet<long>();
            foreach (var id in changed)
            {
                var el = doc.GetElement(ElementIdCompat.ToElementId(id));
                if (el is ElementType) types.Add(id);
                else if (el is Level) levels.Add(id);
            }
            if (types.Count == 0 && levels.Count == 0) return result;
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var t = el.GetTypeId();
                if ((t != null && types.Contains(t.LongValue())) ||
                    (el.LevelId != null && levels.Contains(el.LevelId.LongValue())))
                    result.Add(el.Id.LongValue());
            }
            return result;
        }

        // ── Какие проекты ведутся на сайте ────────────────────────────────

        private static bool IsKnownNotLoaded(string name)
        {
            lock (Gate) return _loaded != null && !_loaded.Contains(name);
        }

        private static void MarkNotLoaded(string name)
        {
            if (_loaded != null) _loaded.Remove(name);
            PendingByDoc.Remove(name);
        }

        /// <summary>Проект только что загружен кнопкой — начинаем вести сразу, не ждём обновления списка.</summary>
        public static void MarkLoaded(string name)
        {
            lock (Gate)
            {
                if (_loaded == null) _loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _loaded.Add(name);
            }
        }

        private static void RefreshLoadedInBackground()
        {
            lock (Gate)
            {
                if (_refreshing || DateTime.UtcNow - _loadedAt < LoadedTtl || !CanSend) return;
                _refreshing = true;
            }
            Task.Run(async () =>
            {
                try
                {
                    var docs = await Session.Client.GetBimDocumentsAsync();
                    lock (Gate)
                    {
                        _loaded = new HashSet<string>(docs, StringComparer.OrdinalIgnoreCase);
                        _loadedAt = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    // Не узнали — считаем «неизвестно»: отправим и узнаем по ответу.
                    PluginLog.Write("[ModelSync] список проектов сайта: " + ex.Message);
                    lock (Gate) _loadedAt = DateTime.UtcNow;
                }
                finally { lock (Gate) _refreshing = false; }
            });
        }

        // ── Накопленное: память и диск ──────────────────────────────────────

        private static Pending Get(string name)
        {
            if (!PendingByDoc.TryGetValue(name, out var p)) PendingByDoc[name] = p = new Pending();
            return p;
        }

        private static void Drop(string name) { lock (Gate) PendingByDoc.Remove(name); }

        private static void Merge(Pending into, Pending from)
        {
            if (from == null) return;
            into.Changed.UnionWith(from.Changed);
            into.Deleted.UnionWith(from.Deleted);
            into.Changed.ExceptWith(into.Deleted);
            into.Heal.UnionWith(from.Heal);
        }

        private static bool IsDetached(string name) =>
            string.IsNullOrEmpty(name) || name.IndexOf("отсоединено", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Hash(string name)
        {
            using (var sha = SHA1.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))
                                   .Replace("-", "").Substring(0, 16);
            }
        }

        private static string QueueRoot() => Path.Combine(PluginConfig.DataDir, "model-sync");
        private static string SavedPath(string name) => Path.Combine(QueueRoot(), Hash(name) + ".json");
        private static string QueueDir(string name) => Path.Combine(QueueRoot(), Hash(name));

        /// <summary>Накопленные id (ещё не выгруженные) — на случай падения Revit между правками.</summary>
        private static void Save(string name, Pending p)
        {
            try
            {
                var path = SavedPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new { document = name, changed = p.Changed, deleted = p.Deleted, heal = p.Heal }));
            }
            catch (Exception ex) { PluginLog.Write("[ModelSync] сохранить накопленное: " + ex.Message); }
        }

        private static Pending LoadSaved(string name)
        {
            try
            {
                var path = SavedPath(name);
                if (!File.Exists(path)) return null;
                var s = JsonConvert.DeserializeAnonymousType(File.ReadAllText(path),
                    new { document = "", changed = new long[0], deleted = new long[0], heal = new string[0] });
                return new Pending
                {
                    Changed = new HashSet<long>(s?.changed ?? new long[0]),
                    Deleted = new HashSet<long>(s?.deleted ?? new long[0]),
                    Heal = new HashSet<string>(s?.heal ?? new string[0], StringComparer.OrdinalIgnoreCase),
                };
            }
            catch { return null; }
        }

        private static void DeleteSaved(string name)
        {
            try { File.Delete(SavedPath(name)); } catch { }
        }

        // ── Очередь приращений на диске ─────────────────────────────────────

        /// <summary>Положить приращение в очередь документа; имя файла — время, порядок отправки по нему.</summary>
        private static string Enqueue(string name, byte[] glb)
        {
            var dir = QueueDir(name);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".glb");
            var tmp = file + ".part";
            File.WriteAllBytes(tmp, glb);
            File.Move(tmp, file);   // обход очереди не подхватит недописанный файл
            return file;
        }

        private static string[] QueuedFiles(string name)
        {
            try
            {
                var dir = QueueDir(name);
                if (!Directory.Exists(dir)) return new string[0];
                return Directory.GetFiles(dir, "*.glb").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch { return new string[0]; }
        }

        /// <summary>Отметить время последней синхронизации документа — от него считается час тишины.</summary>
        private static void TouchState(string name, Document doc)
        {
            try
            {
                var state = LoadState(QueueDir(name)) ?? new QueueState { Document = name };
                state.Document = name;
                state.LastStagedUtc = DateTime.UtcNow;
                try { state.RevitUser = doc?.Application?.Username; } catch { }
                SaveState(name, state);
            }
            catch (Exception ex) { PluginLog.Write("[ModelSync] состояние очереди: " + ex.Message); }
        }

        private static void SaveState(string name, QueueState state)
        {
            var dir = QueueDir(name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"), JsonConvert.SerializeObject(state));
        }

        private static QueueState LoadState(string dir)
        {
            var path = Path.Combine(dir, "state.json");
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<QueueState>(File.ReadAllText(path));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
