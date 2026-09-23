using System;
using System.IO;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Report;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Маркеры имени модели живут в общем каталоге (Report\ChecklistCatalog.cs):
    /// его же использует отчёт в TNovDesktop, где нет Revit API.
    /// </summary>
    public static class ModelNameRules
    {
        public static bool IsArOrPof(Document doc) => ContainsAny(doc, ChecklistCatalog.ArOrPofMarkers);

        public static bool IsArModel(Document doc) => ContainsAny(doc, ChecklistCatalog.ArMarkers);

        public static bool IsRebarNoMarkModel(Document doc) => ContainsAny(doc, ChecklistCatalog.RebarNoMarkMarkers);

        public static bool IsNoPartsModel(Document doc) => ContainsAny(doc, ChecklistCatalog.NoPartsMarkers);

        // Маркеры ВК/ОВ — как у сценария ВК ОВ в MEPSpec (без ПТ и ТС)
        public static bool IsVkOvModel(Document doc) => ContainsAny(doc, ChecklistCatalog.VkOvMarkers);

        // Перегрузки по имени модели — для отчёта, который работает без открытого документа.
        // Имя уже очищено от суффикса «_пользователь» (как в журнале синхронизаций).
        public static bool IsArOrPof(string name) => ContainsAny(name, ChecklistCatalog.ArOrPofMarkers);

        public static bool IsArModel(string name) => ContainsAny(name, ChecklistCatalog.ArMarkers);

        public static bool IsRebarNoMarkModel(string name) => ContainsAny(name, ChecklistCatalog.RebarNoMarkMarkers);

        public static bool IsNoPartsModel(string name) => ContainsAny(name, ChecklistCatalog.NoPartsMarkers);

        public static bool IsVkOvModel(string name) => ContainsAny(name, ChecklistCatalog.VkOvMarkers);

        public static bool ContainsAny(string name, params string[] markers) =>
            ChecklistCatalog.ContainsAny(name, markers);

        public static bool ContainsAny(Document doc, params string[] markers)
        {
            if (doc == null || markers == null || markers.Length == 0) return false;
            return MatchesAny(doc.Title, markers) || MatchesAny(FileName(doc), markers);
        }

        private static string FileName(Document doc)
        {
            try
            {
                string path = doc.PathName;
                if (string.IsNullOrEmpty(path)) return "";
                return Path.GetFileNameWithoutExtension(path) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool MatchesAny(string name, string[] markers)
        {
            if (string.IsNullOrEmpty(name)) return false;

            string cleaned = name;
            try
            {
                string user = RevitAPI.UiApplication?.Application?.Username;
                if (!string.IsNullOrEmpty(user))
                    cleaned = cleaned.Replace("_" + user, "");
            }
            catch { }

            foreach (var marker in markers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                if (cleaned.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
                if (name.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }
}
