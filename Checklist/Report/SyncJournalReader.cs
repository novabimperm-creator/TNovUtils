#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TNovUtils.Checklist.Report
{
    public sealed class ModelSyncInfo
    {
        public string ModelName { get; set; }
        public DateTime LastNonBimSync { get; set; }
        public string LastNonBimUser { get; set; }
        public int NonBimSyncCount { get; set; }
        public DateTime LastAnySync { get; set; }
    }

    /// <summary>
    /// Журнал синхронизаций: {ServerPath}projects\{docName},synchronizes.txt,
    /// строки «дата,пользователь,docName» (пишет TNov/Application.OnSyncCentralEnd).
    /// </summary>
    public static class SyncJournalReader
    {
        public const string FileSuffix = ",synchronizes.txt";

        private static readonly CultureInfo[] DateCultures =
        {
            CultureInfo.CurrentCulture,
            CultureInfo.GetCultureInfo("ru-RU"),
            CultureInfo.InvariantCulture
        };

        /// <summary>
        /// Модели, которые с момента since синхронизировал хотя бы один пользователь не из BIM.
        /// </summary>
        public static List<ModelSyncInfo> Read(string projectsFolder, DateTime since, RolesReader roles, List<string> errors)
        {
            var result = new List<ModelSyncInfo>();
            if (!Directory.Exists(projectsFolder)) return result;

            foreach (string path in Directory.EnumerateFiles(projectsFolder, "*" + FileSuffix))
            {
                try
                {
                    // Быстрый отсев: файл, не менявшийся с начала периода, новых записей не содержит.
                    if (File.GetLastWriteTime(path) < since) continue;

                    string fileName = Path.GetFileName(path);
                    var info = new ModelSyncInfo
                    {
                        ModelName = fileName.Substring(0, fileName.Length - FileSuffix.Length)
                    };

                    foreach (string line in File.ReadLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string[] parts = line.Split(',');
                        if (parts.Length < 2) continue;
                        if (!TryParseDate(parts[0], out DateTime date) || date < since) continue;

                        string user = parts[1].Trim();
                        if (date > info.LastAnySync) info.LastAnySync = date;
                        if (roles.IsBim(user)) continue;

                        info.NonBimSyncCount++;
                        if (date > info.LastNonBimSync)
                        {
                            info.LastNonBimSync = date;
                            info.LastNonBimUser = user;
                        }
                    }

                    if (info.NonBimSyncCount > 0)
                        result.Add(info);
                }
                catch (Exception ex)
                {
                    errors?.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
            return result;
        }

        private static bool TryParseDate(string text, out DateTime date)
        {
            text = text?.Trim();
            foreach (var culture in DateCultures)
            {
                if (DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out date))
                    return true;
            }
            date = default;
            return false;
        }
    }
}
