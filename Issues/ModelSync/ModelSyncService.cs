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
    /// «Модель» TNovPRO: сайт обновляется при каждой синхронизации с центральной
    /// моделью (решение Виктора 2026-09-28, без ночных выгрузок).
    ///
    /// Всё время копим id изменённых и удалённых элементов (DocumentChanged — только
    /// множества чисел, без чтения элементов). После синхронизации выгружаем ИХ
    /// (паспорт + геометрия) и сверочные цифры по категориям и отправляем в фоне.
    /// Не ушло (нет сети, истёк вход) — остаётся на диске и уходит со следующей
    /// синхронизацией. Проекты, которых нет на сайте, не трогаем вовсе.
    ///
    /// Подключение — в TNov/Application.cs: DocumentChanged → <see cref="OnDocumentChanged"/>,
    /// DocumentSynchronizedWithCentral → <see cref="OnSynchronized"/>,
    /// DocumentSaved (файл без совместной работы) → <see cref="OnSaved"/>.
    /// Контракт с сайтом — doc/BIM-ASK-SYNC.md в репозитории TNovPRO.
    /// </summary>
    public static class ModelSyncService
    {
        // Больше — через загрузку частями: у сервера предел тела запроса 50 МБ.
        private const int DirectLimit = 45 * 1024 * 1024;
        private static readonly TimeSpan LoadedTtl = TimeSpan.FromMinutes(10);

        private sealed class Pending
        {
            public HashSet<long> Changed = new HashSet<long>();
            public HashSet<long> Deleted = new HashSet<long>();
            // Самолечение (2026-10-08): категории, которые по сверке сайта
            // разошлись с моделью (ответ прошлой синхронизации). При следующей
            // выгрузке уходят ЦЕЛИКОМ, и сайт сносит у себя лишнее.
            public HashSet<string> Heal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool IsEmpty => Changed.Count == 0 && Deleted.Count == 0;
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

        private static ApiSession Session => IssuesHost.Session;

        /// <summary>
        /// Можно ли отправлять: есть ключ синхронизации с папки раздачи или человек
        /// хоть раз входил в TNovPRO. Ни того, ни другого — молчим.
        /// </summary>
        private static bool CanSend => _corporate || !string.IsNullOrEmpty(_syncKey)
                                       || Session.Tokens.HasRefresh || !string.IsNullOrEmpty(Session.Tokens.AccessToken);

        private static string _syncKey;
        // Корпоративная установка TNov: из сети компании сайт принимает синхронизацию
        // без входа и без ключа (решение Виктора: «при входе в модель автоматически»).
        private static bool _corporate;

        /// <summary>
        /// Вызывается из TNov при запуске Revit. Ключ синхронизации — файл
        /// «tnovpro\sync-key.txt» на корпоративной папке раздачи TNov (serverPath),
        /// а не в коде: репозитории плагина публичные. Для отладки — переменная
        /// TNOVPRO_SYNC_KEY. Читается в фоне: сетевая папка может отвечать секундами.
        /// </summary>
        public static void Configure(string serverPath)
        {
            _corporate = !string.IsNullOrEmpty(serverPath);
            Task.Run(() =>
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
                    if (string.IsNullOrEmpty(key)) return;
                    _syncKey = key;
                    Session.Client.SyncKey = key;
                    PluginLog.Write("[ModelSync] ключ синхронизации найден — синхронизация без входа включена");
                }
                catch (Exception ex) { PluginLog.Write("[ModelSync] ключ синхронизации: " + ex.Message); }
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
        public static void OnSynchronized(Document doc) => Flush(doc, "синхронизация");

        /// <summary>Файл без совместной работы: сохранение — то же, что синхронизация.</summary>
        public static void OnSaved(Document doc)
        {
            if (doc == null || doc.IsWorkshared) return;
            Flush(doc, "сохранение");
        }

        /// <summary>
        /// Выгрузить накопленное и отправить. Вызывается в контексте Revit API
        /// (обработчик события): чтение модели — здесь, сеть — в фоне.
        /// </summary>
        private static void Flush(Document doc, string why)
        {
            try
            {
                if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
                var name = ModelExporter.DocumentName(doc);
                if (IsDetached(name)) return;
                if (IsKnownNotLoaded(name)) { Drop(name); return; }
                if (!CanSend) { Drop(name); return; }

                Pending snap;
                lock (Gate)
                {
                    if (Sending.Contains(name)) return;   // прошлая отправка ещё идёт — доедет со следующей
                    var p = Get(name);
                    Merge(p, LoadSaved(name));
                    snap = new Pending
                    {
                        Changed = new HashSet<long>(p.Changed),
                        Deleted = new HashSet<long>(p.Deleted),
                        Heal = new HashSet<string>(p.Heal, StringComparer.OrdinalIgnoreCase),
                    };
                    Sending.Add(name);
                }

                try { Session.Client.RevitUser = doc.Application?.Username; } catch { }
                var clock = Stopwatch.StartNew();
                var changed = Expand(doc, snap.Changed);
                // Самолечение: разъехавшиеся категории уходят целиком — сайт
                // дополнит недостающее и снесёт у себя то, чего в модели нет.
                if (snap.Heal.Count > 0) changed.UnionWith(CollectCategories(doc, snap.Heal));
                var counts = ModelExporter.CountCategories(doc);
                var glb = ModelExporter.ExportElements(doc, changed, snap.Deleted, counts, DateTime.UtcNow,
                                                       snap.Heal.Count > 0 ? snap.Heal : null);
                clock.Stop();
                PluginLog.Write($"[ModelSync] {why} «{name}»: изменено {snap.Changed.Count}"
                                + (changed.Count != snap.Changed.Count ? $" (с экземплярами типов и лечением {changed.Count})" : "")
                                + (snap.Heal.Count > 0 ? $", лечение [{string.Join(", ", snap.Heal)}]" : "")
                                + $", удалено {snap.Deleted.Count}, {glb.Length / 1024} КБ, {clock.ElapsedMilliseconds} мс");

                Task.Run(() => SendAsync(name, glb, snap));
            }
            catch (Exception ex)
            {
                PluginLog.Write("[ModelSync] выгрузка: " + ex);
                lock (Gate) Sending.Remove(ModelExporter.DocumentName(doc));
            }
        }

        private static async Task SendAsync(string name, byte[] glb, Pending sent)
        {
            try
            {
                ApiClient.SyncOutcome outcome;
                if (glb.Length <= DirectLimit)
                {
                    outcome = await Session.Client.SyncModelAsync(name, glb);
                }
                else
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "tnovpro-sync-" + Guid.NewGuid().ToString("N") + ".glb");
                    try
                    {
                        File.WriteAllBytes(tmp, glb);
                        var uploadId = await Session.Client.UploadChunkedAsync(tmp);
                        outcome = await Session.Client.SyncModelAsync(name, uploadId);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                lock (Gate)
                {
                    if (!outcome.Loaded) MarkNotLoaded(name);
                    // Убираем только отправленное: пока шла отправка, человек мог
                    // править дальше — это уйдёт со следующей синхронизацией.
                    var p = Get(name);
                    p.Changed.ExceptWith(sent.Changed);
                    p.Deleted.ExceptWith(sent.Deleted);
                    // Лечение: отправленные категории — долечены; то, что сверка
                    // нашла СЕЙЧАС, дошлём целиком при следующей синхронизации.
                    p.Heal.ExceptWith(sent.Heal);
                    if (outcome.Loaded) p.Heal.UnionWith(outcome.DriftCategories);
                    DeleteSaved(name);
                    if (!p.IsEmpty || p.Heal.Count > 0) Save(name, p);
                }
                PluginLog.Write(!outcome.Loaded
                    ? $"[ModelSync] «{name}»: проект не загружен на сайт — не синхронизируем"
                    : outcome.DriftCategories.Count > 0
                        ? $"[ModelSync] «{name}»: сайт обновлён; расходится [{string.Join(", ", outcome.DriftCategories)}] — дошлём целиком при следующей синхронизации"
                        : $"[ModelSync] «{name}»: сайт обновлён");
            }
            catch (Exception ex)
            {
                // Нет сети, истёк вход — не теряем: на диск, уйдёт со следующей синхронизацией.
                PluginLog.Write($"[ModelSync] «{name}»: не отправлено ({ex.Message}) — отправим со следующей синхронизацией");
                lock (Gate) Save(name, Get(name));
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

        private static string SavedPath(string name)
        {
            using (var sha = SHA1.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))
                                       .Replace("-", "").Substring(0, 16);
                return Path.Combine(PluginConfig.DataDir, "model-sync", hash + ".json");
            }
        }

        private static void Save(string name, Pending p)
        {
            try
            {
                var path = SavedPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new { document = name, changed = p.Changed, deleted = p.Deleted, heal = p.Heal }));
            }
            catch (Exception ex) { PluginLog.Write("[ModelSync] сохранить неотправленное: " + ex.Message); }
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
    }
}
