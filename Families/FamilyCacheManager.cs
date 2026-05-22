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
                File.WriteAllText(CacheFile, json);
                File.WriteAllText(CacheTimeFile, DateTime.Now.ToString("O"));
            }
            catch { }
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

        public static FamilyCacheItem UpdateOrCreateItem(string filePath, string category, DateTime currentModified)
        {
            var cache = LoadCache();
            if (cache.TryGetValue(filePath, out var existing))
            {
                if (existing.LastModified != currentModified)
                {
                    existing.LastModified = currentModified;
                    existing.VersionNumber++;
                    existing.VersionString = $"v{existing.VersionNumber}";
                }
                existing.Category = category;
                cache[filePath] = existing;
                SaveCache(cache);
                return existing;
            }
            else
            {
                var newItem = new FamilyCacheItem
                {
                    FullPath = filePath,
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    Category = category,
                    LastModified = currentModified,
                    VersionNumber = 1,
                    VersionString = "v1"
                };
                cache[filePath] = newItem;
                SaveCache(cache);
                return newItem;
            }
        }
    }
}