using System;
using System.IO;

namespace TNovUtils
{
    public static class UserNameHelper
    {
        private static string _cachedUserName = null;

        /// <summary>
        /// Возвращает имя пользователя Revit из Revit.ini (2022).
        /// Если не удалось — возвращает имя текущего пользователя Windows.
        /// </summary>
        public static string GetCurrentUserName()
        {
            if (_cachedUserName != null)
                return _cachedUserName;

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string iniPath = Path.Combine(appData, "Autodesk", "Revit", "Autodesk Revit 2022", "Revit.ini");

                if (File.Exists(iniPath))
                {
                    string[] lines = File.ReadAllLines(iniPath);
                    bool inPartitions = false;
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed == "[Partitions]")
                            inPartitions = true;
                        else if (inPartitions && trimmed.StartsWith("Username="))
                        {
                            _cachedUserName = trimmed.Split('=')[1].Trim();
                            break;
                        }
                        else if (trimmed.StartsWith("[") && inPartitions)
                            break;
                    }
                }
            }
            catch { /* fallback */ }

            if (string.IsNullOrEmpty(_cachedUserName))
                _cachedUserName = Environment.UserName;

            return _cachedUserName;
        }
    }
}