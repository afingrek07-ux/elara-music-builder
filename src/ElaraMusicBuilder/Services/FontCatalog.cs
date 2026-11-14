using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace ElaraMusicBuilder.Services
{
    public class FontEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>Шрифты Windows и импортированные пользователем шрифты (TTF/OTF/TTC).</summary>
    public static class FontCatalog
    {
        private static List<FontEntry> _cache;

        public static string WindowsFonts => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        private static string UserFonts => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

        public static List<FontEntry> All(bool refresh = false)
        {
            if (_cache != null && !refresh) return _cache;
            var list = new List<FontEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { AppPaths.Fonts, UserFonts, WindowsFonts })
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir).Where(f => MediaTypes.Fonts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())).ToList(); }
                catch { continue; }
                foreach (var f in files)
                {
                    string name = DisplayName(f);
                    if (seen.Add(name)) list.Add(new FontEntry { Name = name, Path = f });
                }
            }
            _cache = list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return _cache;
        }

        private static string DisplayName(string file)
        {
            try
            {
                var gt = new GlyphTypeface(new Uri(file));
                string family = gt.Win32FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var f) ? f : gt.Win32FamilyNames.Values.FirstOrDefault();
                string face = gt.Win32FaceNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var fc) ? fc : gt.Win32FaceNames.Values.FirstOrDefault();
                if (!string.IsNullOrEmpty(family))
                    return string.IsNullOrEmpty(face) || face.Equals("Regular", StringComparison.OrdinalIgnoreCase) ? family : family + " " + face;
            }
            catch { }
            return System.IO.Path.GetFileNameWithoutExtension(file);
        }

        /// <summary>Копирует шрифт в папку программы, чтобы проект не зависел от исходного места файла.</summary>
        public static FontEntry Import(string file)
        {
            var dst = System.IO.Path.Combine(AppPaths.Fonts, System.IO.Path.GetFileName(file));
            File.Copy(file, dst, true);
            All(true);
            return _cache.FirstOrDefault(e => string.Equals(e.Path, dst, StringComparison.OrdinalIgnoreCase))
                   ?? new FontEntry { Name = System.IO.Path.GetFileNameWithoutExtension(dst), Path = dst };
        }

        /// <summary>Шрифт, который реально будет использован (с запасными вариантами, поддерживающими кириллицу).</summary>
        public static string Resolve(string preferred, bool title)
        {
            if (!string.IsNullOrEmpty(preferred) && File.Exists(preferred)) return preferred;
            var fallbacks = title
                ? new[] { "georgia.ttf", "cambria.ttc", "times.ttf", "segoeui.ttf", "arial.ttf" }
                : new[] { "segoeui.ttf", "arial.ttf", "tahoma.ttf", "verdana.ttf" };
            foreach (var f in fallbacks)
            {
                var p = System.IO.Path.Combine(WindowsFonts ?? "", f);
                if (File.Exists(p)) return p;
            }
            var any = All().FirstOrDefault();
            if (any != null) return any.Path;
            throw new FileNotFoundException("В системе не найден ни один шрифт для надписей.");
        }
    }
}
