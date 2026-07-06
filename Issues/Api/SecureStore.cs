using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TNovUtils.Issues.Config;

namespace TNovUtils.Issues.Api
{
    /// <summary>Шифрование «по пользователю Windows» (DPAPI) для секретов на диске.</summary>
    internal static class Dpapi
    {
        public static byte[] Protect(byte[] data) =>
            ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        public static byte[] Unprotect(byte[] data) =>
            ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Хранилище токенов: access — только в памяти (живёт 2ч), refresh — на диске
    /// под DPAPI (переживает перезапуск Revit, недоступен другим пользователям ОС).
    /// </summary>
    public sealed class TokenStore
    {
        private readonly string _refreshPath = Path.Combine(PluginConfig.DataDir, "refresh.bin");

        public string AccessToken { get; set; }
        public string RefreshToken { get; private set; }
        public UserInfo User { get; set; }

        public bool HasRefresh => !string.IsNullOrEmpty(RefreshToken);

        // Кто я: после тихого restore (refresh) User пуст — берём userId/role из payload
        // access-токена (JWT, base64url; без проверки подписи — только для UI-решений,
        // серверные права всё равно проверяет бэкенд).
        public string CurrentUserId => JwtClaim("userId") ?? (User != null ? User.Id : null);
        public string CurrentUserRole => JwtClaim("role") ?? (User != null ? User.Role : null);

        private string JwtClaim(string name)
        {
            try
            {
                var t = AccessToken;
                if (string.IsNullOrEmpty(t)) return null;
                var parts = t.Split('.');
                if (parts.Length < 2) return null;
                var s = parts[1].Replace('-', '+').Replace('_', '/');
                switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(s));
                return (string)Newtonsoft.Json.Linq.JObject.Parse(json)[name];
            }
            catch { return null; }
        }

        public TokenStore() { LoadRefresh(); }

        public void SetTokens(string access, string refresh)
        {
            if (!string.IsNullOrEmpty(access)) AccessToken = access;
            if (!string.IsNullOrEmpty(refresh)) SaveRefresh(refresh);
        }

        private void LoadRefresh()
        {
            try
            {
                if (File.Exists(_refreshPath))
                    RefreshToken = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(_refreshPath)));
            }
            catch { RefreshToken = null; }
        }

        private void SaveRefresh(string token)
        {
            RefreshToken = token;
            try { File.WriteAllBytes(_refreshPath, Dpapi.Protect(Encoding.UTF8.GetBytes(token))); }
            catch { /* best-effort */ }
        }

        public void Clear()
        {
            AccessToken = null;
            RefreshToken = null;
            User = null;
            try { if (File.Exists(_refreshPath)) File.Delete(_refreshPath); } catch { }
        }
    }

    /// <summary>
    /// Персистентность cookie между запусками. Нужна для device-trust: TNovPRO
    /// помечает устройство доверенным через cookie — сохраняя её, плагин не
    /// просит 2FA при каждом входе (см. docs/PROMPT-step2d-desktop-auth.md).
    /// </summary>
    public sealed class CookiePersistence
    {
        private readonly string _path = Path.Combine(PluginConfig.DataDir, "cookies.bin");

        [Serializable]
        private struct Rec { public string Name, Value, Domain, Path; }

        public void Load(CookieContainer container, Uri baseUri)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(_path)));
                var recs = Json.Deserialize<List<Rec>>(json);
                if (recs == null) return;
                foreach (var r in recs)
                {
                    try
                    {
                        container.Add(new Cookie(r.Name, r.Value, string.IsNullOrEmpty(r.Path) ? "/" : r.Path,
                            string.IsNullOrEmpty(r.Domain) ? baseUri.Host : r.Domain));
                    }
                    catch { /* skip malformed */ }
                }
            }
            catch { /* ignore */ }
        }

        public void Save(CookieContainer container, Uri baseUri)
        {
            try
            {
                var list = new List<Rec>();
                foreach (Cookie c in container.GetCookies(baseUri))
                    list.Add(new Rec { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path });
                File.WriteAllBytes(_path, Dpapi.Protect(Encoding.UTF8.GetBytes(Json.Serialize(list))));
            }
            catch { /* best-effort */ }
        }
    }
}
