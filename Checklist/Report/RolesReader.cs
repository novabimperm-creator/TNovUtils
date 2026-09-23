#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

namespace TNovUtils.Checklist.Report
{
    /// <summary>
    /// roles.txt: «login,DEPT[,role]», строки «::» — комментарии.
    /// Логин сравнивается точно, без учёта регистра (как в RoleManager).
    /// </summary>
    public sealed class RolesReader
    {
        private readonly Dictionary<string, string> _departments =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool Loaded { get; }

        public RolesReader(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("::")) continue;

                string[] parts = line.Split(',');
                if (parts.Length < 2) continue;

                string login = Normalize(parts[0]);
                string dept = parts[1].Trim();
                if (login.Length > 0 && dept.Length > 0)
                    _departments[login] = dept;
            }
            Loaded = true;
        }

        /// <summary>Пользователи, которых нет в roles.txt, считаются не-BIM.</summary>
        public bool IsBim(string user)
        {
            string login = Normalize(user);
            return login.Length > 0
                && _departments.TryGetValue(login, out string dept)
                && string.Equals(dept, "BIM", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return "";
            string login = user.Trim();
            int slash = login.LastIndexOf('\\');
            if (slash >= 0) login = login.Substring(slash + 1);
            int at = login.IndexOf('@');
            if (at >= 0) login = login.Substring(0, at);
            return login.Trim();
        }
    }
}
