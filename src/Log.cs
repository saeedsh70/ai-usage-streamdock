using System;
using System.IO;
using System.Text;

namespace AiUsageDeck
{
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string Folder
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("AI_USAGE_DECK_HOME");
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "AIUsageDeck");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    string file = Path.Combine(Folder, "plugin.log");
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string old = file + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }
                    File.AppendAllText(file,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never take the plugin down.
            }
        }
    }
}
