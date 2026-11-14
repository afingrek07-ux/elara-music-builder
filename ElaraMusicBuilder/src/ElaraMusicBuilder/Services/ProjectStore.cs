using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    /// <summary>Настройки программы (последние проекты, первый запуск, восстановление после сбоя).</summary>
    public class AppSettings
    {
        public bool WelcomeShown { get; set; }
        public bool CleanExit { get; set; } = true;
        public List<string> RecentProjects { get; set; } = new List<string>();
        public string LastOutputFolder { get; set; }

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8), Opts) ?? new AppSettings();
            }
            catch (Exception ex) { Log.Warn("Настройки повреждены, используются стандартные: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            try { File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Opts), Encoding.UTF8); }
            catch (Exception ex) { Log.Warn("Не удалось сохранить настройки: " + ex.Message); }
        }

        public void AddRecent(string path)
        {
            RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentProjects.Insert(0, path);
            if (RecentProjects.Count > 12) RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
            Save();
        }
    }

    /// <summary>Сохранение и загрузка проектов .elara (JSON, UTF-8).</summary>
    public static class ProjectStore
    {
        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static void Save(ProjectModel p, string path)
        {
            var json = JsonSerializer.Serialize(p, Opts);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, true); // атомарная замена — файл не повредится при сбое
        }

        public static ProjectModel Load(string path)
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var p = JsonSerializer.Deserialize<ProjectModel>(json, Opts) ?? throw new InvalidDataException("Файл проекта пустой.");
            p.Tracks ??= new System.Collections.ObjectModel.ObservableCollection<TrackItem>();
            p.Audio ??= new AudioSettings();
            p.Background ??= new BackgroundSettings();
            p.Visualizer ??= new VisualizerSettings();
            p.Overlay ??= new OverlaySettings();
            p.Export ??= new ExportSettings();
            p.YouTube ??= new YouTubeMeta();
            return p;
        }

        /// <summary>Список отсутствующих файлов проекта (для предупреждения пользователя).</summary>
        public static List<string> FindMissing(ProjectModel p)
        {
            var missing = new List<string>();
            foreach (var t in p.Tracks)
            {
                t.IsMissing = !File.Exists(t.FilePath);
                if (t.IsMissing) missing.Add("Трек: " + t.FilePath);
            }
            if (!string.IsNullOrEmpty(p.Background.FilePath) && !File.Exists(p.Background.FilePath)) missing.Add("Фон: " + p.Background.FilePath);
            if (!string.IsNullOrEmpty(p.Overlay.LogoPath) && !File.Exists(p.Overlay.LogoPath)) missing.Add("Логотип: " + p.Overlay.LogoPath);
            if (!string.IsNullOrEmpty(p.Overlay.TitleFont) && !File.Exists(p.Overlay.TitleFont)) missing.Add("Шрифт: " + p.Overlay.TitleFont);
            if (!string.IsNullOrEmpty(p.Overlay.BodyFont) && !File.Exists(p.Overlay.BodyFont)) missing.Add("Шрифт: " + p.Overlay.BodyFont);
            return missing;
        }
    }
}
