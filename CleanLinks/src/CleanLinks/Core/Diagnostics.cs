using System;
using System.IO;
using System.Text;

namespace CleanLinks.Core
{
    /// <summary>
    /// Простой файловый лог. Настройки отображения RVT-связей в API 2022 прочитать нельзя,
    /// поэтому единственный способ понять, что произошло на самом деле — записать всё,
    /// что API всё-таки показывает: какие параметры контролируются до и после операции.
    /// Лог никогда не должен ронять команду.
    /// </summary>
    public static class Diagnostics
    {
        public static string LogPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CleanLinks");
                return Path.Combine(dir, "log.txt");
            }
        }

        public static void StartSession(string title)
        {
            Write(Environment.NewLine
                  + "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + title + " ===");
        }

        public static void Write(string line)
        {
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Логи не важнее работы команды.
            }
        }
    }
}
