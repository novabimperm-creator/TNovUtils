using System;
using System.IO;

namespace TNovUtils.Issues.Revit
{
    /// <summary>
    /// Простейший файловый лог для диагностики — в первую очередь НАТИВНЫХ вылетов Revit
    /// (access violation в геометрическом API), которые не оставляют управляемого стека и не
    /// ловятся try/catch. Пишет в %LOCALAPPDATA%\TNovProIssues\plugin.log и сбрасывает на диск
    /// ПОСЛЕ КАЖДОЙ записи, поэтому при вылете последняя строка указывает, где именно упало.
    /// </summary>
    public static class PluginLog
    {
        private static readonly object _gate = new object();
        private static string _path;
        private static bool _inited;

        public static string FilePath
        {
            get { if (!_inited) { _path = Init(); _inited = true; } return _path; }
        }

        private static string Init()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TNovProIssues");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "plugin.log");
            }
            catch { return null; }
        }

        public static void Write(string message)
        {
            var p = FilePath;
            if (p == null) return;
            try
            {
                lock (_gate)
                using (var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs))
                {
                    sw.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message);
                    sw.Flush();
                }
            }
            catch { /* лог не должен мешать работе плагина */ }
        }
    }
}
