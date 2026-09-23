using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TNovCommon;

namespace TNovUtils
{
    public static class FamilyCacheManager
    {
        /*
        private static readonly string CacheFolder = AppConfig.FamilyRequestsPath;
        private static readonly string CacheFile = Path.Combine(CacheFolder, "families_cache.json");
        private static readonly string CacheTimeFile = Path.Combine(CacheFolder, "cache_time.txt");
        
        static FamilyCacheManager()
        {
            try { if (!Directory.Exists(CacheFolder)) Directory.CreateDirectory(CacheFolder); }
            catch { }
        }
        */
        public static Dictionary<string, FamilyCacheItem> LoadCache()
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string CacheFile = config.ServerPath + @"familyrequests\families_cache.json";

            if (!File.Exists(CacheFile))
                return new Dictionary<string, FamilyCacheItem>();

            try
            {
                string json = File.ReadAllText(CacheFile);
                var items = JsonConvert.DeserializeObject<List<FamilyCacheItem>>(json);
                return items.ToDictionary(i => i.FullPath, i => i);
            }
            catch
            {
                return new Dictionary<string, FamilyCacheItem>();
            }
        }

        public static void SaveCache(Dictionary<string, FamilyCacheItem> cache)
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string CacheFile = config.ServerPath + @"familyrequests\families_cache.json";
            string CacheTimeFile = config.ServerPath + @"familyrequests\cache_time.txt";

            try
            {
                var items = cache.Values.ToList();
                string json = JsonConvert.SerializeObject(items);//, new JsonSerializerOptions { WriteIndented = true });
                WriteAtomic(CacheFile, json);
                File.WriteAllText(CacheTimeFile, DateTime.Now.ToString("O"));
            }
            catch { }
        }

        /// <summary>
        /// Запись через временный файл в той же папке + File.Replace/Move: читатель
        /// (другой пользователь) никогда не увидит наполовину записанный кэш.
        /// </summary>
        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, content);
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>
        /// Обновить/добавить запись в уже загруженном кэше (без чтения/записи файла).
        /// Потокобезопасно для ConcurrentDictionary — для параллельного сканирования:
        /// LoadCache один раз → ApplyScannedFile на каждый .rfa → SaveCache один раз.
        /// </summary>
        public static FamilyCacheItem ApplyScannedFile(
            System.Collections.Concurrent.ConcurrentDictionary<string, FamilyCacheItem> cache,
            string filePath, string category, DateTime currentModified)
        {
            var item = cache.GetOrAdd(filePath, key => new FamilyCacheItem
            {
                FullPath = key,
                Name = Path.GetFileNameWithoutExtension(key),
                Category = category,
                VersionNumber = 0,
                VersionString = "v0"
            });

            lock (item)
            {
                // Новая запись: LastModified по умолчанию ≠ дате файла → v1, как раньше.
                if (item.LastModified != currentModified)
                {
                    item.LastModified = currentModified;
                    item.VersionNumber++;
                    item.VersionString = $"v{item.VersionNumber}";
                }
                item.Category = category;
            }
            return item;
        }

        /// <summary>
        /// Кэш устарел по времени (старше 1 часа)?
        /// </summary>
        public static bool IsCacheExpired()
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string CacheTimeFile = config.ServerPath + @"familyrequests\cache_time.txt";

            if (!File.Exists(CacheTimeFile))
                return true;

            try
            {
                string content = File.ReadAllText(CacheTimeFile).Trim();
                if (DateTime.TryParse(content, out DateTime lastUpdate))
                    return DateTime.Now - lastUpdate > TimeSpan.FromHours(1);
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Кэш неполный, если количество папок *_Семейства* в корне не совпадает с количеством категорий в кэше
        /// </summary>
        public static bool IsCacheIncomplete(string libraryPath)
        {
            // Получаем список папок *_Семейства* в корне библиотеки
            string[] actualFolders;
            try
            {
                actualFolders = Directory.GetDirectories(libraryPath, "*_Семейства*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return true; // если не можем прочитать папки – считаем кэш недействительным
            }

            var cache = LoadCache();
            // Категории в кэше (уникальные)
            var cachedCategories = cache.Values.Select(item => item.Category).Distinct().ToList();

            return actualFolders.Length != cachedCategories.Count;
        }

        /// <summary>
        /// Одиночное обновление: читает и пишет весь families_cache.json.
        /// Не вызывать в цикле по файлам — для сканирования есть <see cref="ApplyScannedFile"/>.
        /// </summary>
        public static FamilyCacheItem UpdateOrCreateItem(string filePath, string category, DateTime currentModified)
        {
            var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, FamilyCacheItem>(LoadCache());
            var item = ApplyScannedFile(cache, filePath, category, currentModified);
            SaveCache(new Dictionary<string, FamilyCacheItem>(cache));
            return item;
        }
    }
}