using System;
using System.IO;
using Autodesk.Revit.DB;
using TNovCommon;

namespace TNovUtils.Checklist.Checks
{
    public static class ModelNameRules
    {
        public static bool IsArOrPof(Document doc) =>
            ContainsAny(doc, "-АР-", "_АР", "-ПОФ-", "_ПОФ");

        public static bool IsArModel(Document doc) =>
            ContainsAny(doc, "-АР-", "_АР");

        public static bool IsRebarNoMarkModel(Document doc) =>
            ContainsAny(doc, "-КЖ-", "_КЖ", "-КР-", "_КР", "-АР-", "_АР");

        public static bool IsNoPartsModel(Document doc) =>
            ContainsAny(doc, "-АР-", "_АР", "-ПОФ-", "_ПОФ", "-КР-", "_КР", "-КЖ-", "_КЖ");

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
