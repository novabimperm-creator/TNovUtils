using System;

namespace TNovUtils
{
    public static class RevitContext
    {
        public static string CurrentProjectPath { get; set; }
        public static string CurrentProjectDisplayName { get; set; }

        /// <summary>
        /// Имя проекта из базы CDE, если оно было найдено по пути модели.
        /// </summary>
        public static string CurrentProjectNameFromCde { get; set; }

        public static string CleanProjectDisplayName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return rawName;

            string userName = UserNameHelper.GetCurrentUserName();
            char[] separators = { ' ', '_' };
            int lastSepPos = rawName.LastIndexOfAny(separators);

            if (lastSepPos >= 0)
            {
                string lastPart = rawName.Substring(lastSepPos + 1);
                if (string.Equals(lastPart, userName, StringComparison.OrdinalIgnoreCase))
                    return rawName.Substring(0, lastSepPos);
            }
            return rawName;
        }
    }
}