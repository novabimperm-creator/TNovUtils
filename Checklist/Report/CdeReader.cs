#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TNovUtils.Checklist.Report
{
    public sealed class CdeProject
    {
        public string Code { get; set; }
        public string NwcFolder { get; set; }
        public string Status { get; set; }

        public bool IsStopped =>
            string.Equals(Status, "stop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CDE.txt: «Код,Путь к NWC,active|stop». Путь может содержать запятые —
    /// берём всё между первой и последней запятой (как CdeViewModel).
    /// </summary>
    public sealed class CdeReader
    {
        private readonly List<CdeProject> _projects = new List<CdeProject>();

        public bool Loaded { get; }

        public CdeReader(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split(',');
                if (parts.Length < 3) continue;

                string code = parts[0].Trim();
                if (code.Length == 0) continue;

                _projects.Add(new CdeProject
                {
                    Code = code,
                    Status = parts[parts.Length - 1].Trim(),
                    NwcFolder = string.Join(",", parts.Skip(1).Take(parts.Length - 2)).Trim()
                });
            }
            Loaded = true;
        }

        /// <summary>
        /// Проект, код которого входит в имя модели. При нескольких совпадениях —
        /// самый длинный код, чтобы короткий код-префикс не перехватывал чужие модели.
        /// </summary>
        public CdeProject Find(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return null;
            return _projects
                .Where(p => modelName.IndexOf(p.Code, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(p => p.Code.Length)
                .FirstOrDefault();
        }
    }
}
