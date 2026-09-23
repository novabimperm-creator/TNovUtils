#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace TNovUtils.Checklist.Report
{
    public sealed class ReportResult
    {
        public DateTime Since { get; set; }
        public DateTime BuiltAt { get; set; }
        public List<ModelReportRow> Rows { get; set; } = new List<ModelReportRow>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Собирает отчёт из файлов на сервере, не открывая модели и не трогая Revit API —
    /// поэтому безопасно вызывать из фонового потока.
    /// </summary>
    public static class ModelReportBuilder
    {
        public const int PeriodDays = 7;

        public static ReportResult Build(string serverPath)
        {
            var now = DateTime.Now;
            var result = new ReportResult { BuiltAt = now, Since = now.AddDays(-PeriodDays) };

            string projects = Path.Combine(serverPath, "projects");
            var roles = new RolesReader(Path.Combine(serverPath, "roles.txt"));
            var cde = new CdeReader(Path.Combine(serverPath, "CDE.txt"));
            if (!roles.Loaded) result.Warnings.Add("Не найден roles.txt — все пользователи считаются не-BIM.");
            if (!cde.Loaded) result.Warnings.Add("Не найден CDE.txt — актуальность NWC не оценивается.");

            var models = SyncJournalReader.Read(projects, result.Since, roles, result.Warnings);


            foreach (var sync in models.OrderByDescending(m => m.LastNonBimSync))
            {
                var row = new ModelReportRow
                {
                    ModelName = sync.ModelName,
                    LastChanged = sync.LastNonBimSync,
                    LastUser = sync.LastNonBimUser,
                    SyncCount = sync.NonBimSyncCount
                };
                row.Auto = BuildAuto(projects, sync);
                row.Bim = BuildBim(projects, sync);
                row.Nwc = BuildNwc(cde, sync);
                result.Rows.Add(row);
            }
            return result;
        }

        private static ChecksSummary BuildAuto(string projects, ModelSyncInfo sync)
        {
            var summary = new ChecksSummary();
            List<SavedAutoCheck> saved;
            try
            {
                saved = ReadJson<List<SavedAutoCheck>>(JsonPath(projects, sync.ModelName, "autocheck"))
                        ?? new List<SavedAutoCheck>();
            }
            catch (Exception ex)
            {
                summary.Error = ex.Message;
                summary.Level = ReportLevel.High;
                return summary;
            }

            foreach (var check in ChecklistCatalog.AutoChecksFor(sync.ModelName))
            {
                summary.Total++;
                var item = saved.FirstOrDefault(i => i.Number == check.Number);
                // Пункт без title — «заготовка» журнала: проверку ещё не запускали.
                bool neverRun = item == null || string.IsNullOrWhiteSpace(item.Title);

                if (neverRun || IsStale(item.CreationDate, sync.LastNonBimSync, ChecklistCatalog.AutoStaleCalendarDays))
                    summary.Stale.Add(check.Title);
                if (!neverRun && !item.IsChecked)
                    summary.Failed.Add(check.Title);
            }

            summary.Level = ReportLevelRules.FromChecks(summary.Total, summary.Failed.Count, summary.Stale.Count);
            return summary;
        }

        private static ChecksSummary BuildBim(string projects, ModelSyncInfo sync)
        {
            var summary = new ChecksSummary();
            List<SavedBimCheck> saved;
            try
            {
                saved = ReadJson<List<SavedBimCheck>>(JsonPath(projects, sync.ModelName, "BIM проверки"))
                        ?? new List<SavedBimCheck>();
            }
            catch (Exception ex)
            {
                summary.Error = ex.Message;
                summary.Level = ReportLevel.High;
                return summary;
            }

            foreach (var item in ChecklistCatalog.BimChecksFor(sync.ModelName))
            {
                summary.Total++;

                var state = saved.FirstOrDefault(s => s.Id == item.Id);
                bool neverTouched = state == null || state.CreatedAt.Year < 2000;

                if (neverTouched || IsStale(state.CreatedAt, sync.LastNonBimSync, ChecklistCatalog.BimStaleCalendarDays))
                    summary.Stale.Add(item.Title);
                if (state == null || !state.IsChecked)
                    summary.Failed.Add(item.Title);
            }

            summary.Level = ReportLevelRules.FromChecks(summary.Total, summary.Failed.Count, summary.Stale.Count);
            return summary;
        }

        private static NwcSummary BuildNwc(CdeReader cde, ModelSyncInfo sync)
        {
            var summary = new NwcSummary();
            if (!cde.Loaded)
            {
                summary.Note = "нет CDE.txt";
                return summary;
            }

            var project = cde.Find(sync.ModelName);
            if (project == null)
            {
                summary.Note = "проект не в CDE";
                return summary;
            }
            if (project.IsStopped)
            {
                summary.Note = "проект остановлен";
                return summary;
            }

            try
            {
                summary.NwcPath = Path.Combine(project.NwcFolder, NwcFileName(sync.ModelName));
                if (!File.Exists(summary.NwcPath))
                {
                    summary.Level = ReportLevel.High;
                    summary.Note = "NWC не найден";
                    return summary;
                }

                var nwcDate = File.GetLastWriteTime(summary.NwcPath);
                double lag = Math.Max(0, (sync.LastNonBimSync - nwcDate).TotalDays);
                summary.NwcDate = nwcDate;
                summary.LagDays = lag;
                summary.Level = ReportLevelRules.FromNwcLag(lag);
            }
            catch (Exception ex)
            {
                summary.Level = ReportLevel.High;
                summary.Note = "ошибка: " + ex.Message;
            }
            return summary;
        }

        /// <summary>
        /// Допуск как в окне Чек-листа (7 дней автопроверки, 30 — BIM), но отсчёт не от сегодня,
        /// а от последней синхронизации проектировщика: проверка устарела, если между ней
        /// и синхронизацией прошло не меньше toleranceDays календарных дней.
        /// </summary>
        private static bool IsStale(DateTime checkedAt, DateTime lastSync, int toleranceDays) =>
            (lastSync.Date - checkedAt.Date).TotalDays >= toleranceDays;

        /// <summary>Имя NWC — как в пакетном экспорте TNovAuto: без «_отсоединено».</summary>
        private static string NwcFileName(string modelName)
        {
            string name = modelName;
            if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            const string detached = "_отсоединено";
            if (name.EndsWith(detached, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - detached.Length);
            return name + ".nwc";
        }

        /// <summary>Путь — как в JsonDataService.GetJsonPath: {docName},{name}.json.</summary>
        private static string JsonPath(string projects, string modelName, string name) =>
            Path.Combine(projects, $"{modelName},{name}.json");

        private static T ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
                }
                catch (IOException) when (attempt < 2)
                {
                    // Файл может быть занят окном Чек-листа у пользователя — как в JsonDataService.
                    Thread.Sleep(300);
                }
            }
        }
    }
}

namespace TNovUtils.Checklist.Report
{
    // Минимальные копии форматов JSON Чек-листа — без TNovCommon, чтобы файл собирался и в TNovDesktop.

    /// <summary>Пункт {docName},autocheck.json (TNovCommon.AutoCheckItem).</summary>
    public sealed class SavedAutoCheck
    {
        [JsonProperty("Number")] public int Number { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("is_done")] public bool IsChecked { get; set; }
        [JsonProperty("created_at")] public DateTime CreationDate { get; set; }
    }

    /// <summary>Пункт {docName},BIM проверки.json (BimCheckStore.SavedState).</summary>
    public sealed class SavedBimCheck
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("is_done")] public bool IsChecked { get; set; }
        [JsonProperty("created_at")] public DateTime CreatedAt { get; set; }
    }
}
