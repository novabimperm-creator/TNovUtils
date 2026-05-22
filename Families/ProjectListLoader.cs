using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TNovCommon;

namespace TNovUtils
{
    public static class ProjectListLoader
    {
        //private static readonly string CdeFilePath = AppConfig.CdeFilePath;

        public static List<string> LoadProjectNames()
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string CdeFilePath = config.ServerPath + "CDE.txt";
            var names = new List<string>();
            try
            {
                if (!File.Exists(CdeFilePath))
                    return names;

                foreach (string line in File.ReadLines(CdeFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    int commaIndex = line.IndexOf(',');
                    string name = (commaIndex >= 0)
                        ? line.Substring(0, commaIndex).Trim()
                        : line.Trim();

                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            catch { }

            return names.Distinct().OrderBy(n => n).ToList();
        }

        public static string FindProjectInPath(string path, List<string> projectNames)
        {
            if (string.IsNullOrEmpty(path) || projectNames == null || projectNames.Count == 0)
                return null;

            return projectNames.FirstOrDefault(name =>
                path.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}