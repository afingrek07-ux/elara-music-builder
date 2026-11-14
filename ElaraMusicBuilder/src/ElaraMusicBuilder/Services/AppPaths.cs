using System;
using System.IO;
using System.Text;

namespace ElaraMusicBuilder.Services
{
    public static class AppPaths
    {
        public static string Root => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElaraMusicBuilder"));
        public static string Cache => Ensure(Path.Combine(Root, "cache"));
        public static string Work => Ensure(Path.Combine(Root, "work"));
        public static string Logs => Ensure(Path.Combine(Root, "logs"));
        public static string Fonts => Ensure(Path.Combine(Root, "fonts"));
        public static string Demo => Ensure(Path.Combine(Root, "demo"));
        public static string SettingsFile => Path.Combine(Root, "settings.json");
        public static string AutosaveFile => Path.Combine(Root, "autosave.elara");

        public static string DefaultOutput
        {
            get
            {
                var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (string.IsNullOrEmpty(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Ensure(Path.Combine(videos, "Elara Music Builder"));
            }
        }

        public static string NewWorkDir(string prefix)
        {
            return Ensure(Path.Combine(Work, prefix + "_" + DateTime.Now.ToString("HHmmss_fff")));
        }

        public static string Ensure(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* файл может быть занят — не критично */ }
        }

        /// <summary>Удаляет старые временные папки при запуске.</summary>
        public static void CleanupWork()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(Work))
                    if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-6)) TryDeleteDir(d);
            }
            catch { }
        }
    }

    public static class Log
    {
        private static readonly object Gate = new object();

        public static string CurrentFile => Path.Combine(AppPaths.Logs, $"log_{DateTime.Now:yyyyMMdd}.txt");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", ex == null ? msg : msg + Environment.NewLine + ex);

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(CurrentFile, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
