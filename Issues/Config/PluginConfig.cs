using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace TNovUtils.Issues.Config
{
    /// <summary>
    /// Конфигурация плагина. По умолчанию — прод TNovPRO. Базовый URL можно
    /// переопределить файлом %APPDATA%\TNovPROIssues\config.json или переменной
    /// окружения TNOVPRO_BASE_URL (удобно для отладки на staging).
    /// </summary>
    public sealed class PluginConfig
    {
        public const string DefaultBaseUrl = "https://tnov.pm-nova.ru";

        public string BaseUrl { get; set; } = DefaultBaseUrl;

        public Uri BaseUri => new Uri(BaseUrl.TrimEnd('/') + "/");

        public static string DataDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TNovPROIssues");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static PluginConfig Load()
        {
            var cfg = new PluginConfig();
            var env = Environment.GetEnvironmentVariable("TNOVPRO_BASE_URL");
            if (!string.IsNullOrWhiteSpace(env)) cfg.BaseUrl = env.Trim();

            try
            {
                var path = Path.Combine(DataDir, "config.json");
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<PluginConfig>(File.ReadAllText(path));
                    if (loaded != null && !string.IsNullOrWhiteSpace(loaded.BaseUrl))
                        cfg.BaseUrl = loaded.BaseUrl.Trim();
                }
            }
            catch { /* fall back to defaults */ }

            return cfg;
        }
    }

    /// <summary>Единые настройки JSON под контракт API (camelCase, как у дома).</summary>
    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static string Serialize(object o) => JsonConvert.SerializeObject(o, Settings);
        public static T Deserialize<T>(string s) => JsonConvert.DeserializeObject<T>(s, Settings);
    }
}
