using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TNovCommon;

namespace TNovUtils
{
    public static class RoleManager
    {
        //private static readonly string RolesFilePath = AppConfig.RolesFilePath;
        private static Dictionary<string, string> _userRoles = null;
        private static readonly object _lock = new object();

        private static void LoadRoles()
        {
            if (_userRoles != null) return;

            lock (_lock)
            {
                if (_userRoles != null) return;

                var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                TNovConfig config = TNovConfigLoad.LoadConfig();
                string RolesFilePath = config.ServerPath + @"roles.txt";

                try
                {
                    if (File.Exists(RolesFilePath))
                    {
                        foreach (string line in File.ReadLines(RolesFilePath))
                        {
                            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("::"))
                                continue;

                            string[] parts = line.Split(',');
                            if (parts.Length >= 2)
                            {
                                string username = parts[0].Trim();
                                string department = parts[1].Trim();
                                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(department))
                                    roles[username] = department;
                            }
                        }
                    }
                }
                catch { }

                _userRoles = roles;
            }
        }

        public static bool CanViewAllRequests()
        {
            LoadRoles();
            string currentUser = UserNameHelper.GetCurrentUserName();
            return _userRoles.TryGetValue(currentUser, out string role) &&
                   string.Equals(role, "BIM", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetCurrentUserRole()
        {
            LoadRoles();
            string currentUser = UserNameHelper.GetCurrentUserName();
            if (_userRoles.TryGetValue(currentUser, out string department))
            {
                switch (department.ToUpperInvariant())
                {
                    case "BIM": return "BIM";
                    case "AR": return "АР";
                    case "ST": return "КР";
                    case "VK": return "ВК";
                    case "OV": return "ОВ";
                    case "EL": return "ЭЛ";
                    case "SS": return "СС";
                    default: return "";
                }
            }
            return "";
        }
    }
}