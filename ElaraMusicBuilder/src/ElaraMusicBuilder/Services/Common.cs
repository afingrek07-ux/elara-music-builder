using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ElaraMusicBuilder.Services
{
    /// <summary>Форматирование чисел и времени. Для FFmpeg всегда используется точка как разделитель.</summary>
    public static class Fmt
    {
        public static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        public static string N3(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        public static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Время для интерфейса: 4:59 или 1:02:03.</summary>
        public static string Time(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(Math.Floor(seconds));
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes}:{ts.Seconds:00}";
        }

        /// <summary>Тайм-код YouTube. Если видео длиннее часа — у всех строк формат 0:00:00.</summary>
        public static string Chapter(double seconds, bool longFormat)
        {
            var ts = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
            return longFormat
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes:00}:{ts.Seconds:00}";
        }

        public static string Bytes(long b)
        {
            double gb = b / 1024.0 / 1024.0 / 1024.0;
            if (gb >= 1) return gb.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " ГБ";
            return (b / 1024.0 / 1024.0).ToString("0", CultureInfo.GetCultureInfo("ru-RU")) + " МБ";
        }
    }

    public static class MediaTypes
    {
        public static readonly string[] Audio = { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg" };
        public static readonly string[] Image = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
        public static readonly string[] Video = { ".mp4", ".mov", ".mkv", ".webm", ".m4v" };
        public static readonly string[] Fonts = { ".ttf", ".otf", ".ttc" };

        private static string Ext(string p) => string.IsNullOrEmpty(p) ? "" : Path.GetExtension(p).ToLowerInvariant();
        public static bool IsAudio(string p) => Audio.Contains(Ext(p));
        public static bool IsImage(string p) => Image.Contains(Ext(p));
        public static bool IsVideo(string p) => Video.Contains(Ext(p));

        public const string AudioFilter = "Аудио (*.mp3;*.wav;*.flac;*.m4a)|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg|Все файлы|*.*";
        public const string BackgroundFilter = "Изображения и видео|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.mp4;*.mov;*.mkv;*.webm|Все файлы|*.*";
        public const string ImageFilter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp";
        public const string FontFilter = "Шрифты (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc";
        public const string ProjectFilter = "Проект Elara (*.elara)|*.elara";
    }

    /// <summary>Работа с цветами в формате #RRGGBB.</summary>
    public static class ColorUtil
    {
        public static bool TryParse(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 255;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 8) hex = hex.Substring(2); // #AARRGGBB → RRGGBB
            if (hex.Length != 6) return false;
            try
            {
                r = Convert.ToByte(hex.Substring(0, 2), 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Цвет для FFmpeg: 0xRRGGBB.</summary>
        public static string Ff(string hex, string fallback = "0xFFFFFF")
        {
            return TryParse(hex, out var r, out var g, out var b) ? $"0x{r:X2}{g:X2}{b:X2}" : fallback;
        }

        /// <summary>Параметры colorchannelmixer, перекрашивающие белое изображение в нужный цвет.</summary>
        public static string Mixer(string hex)
        {
            TryParse(hex, out var r, out var g, out var b);
            return $"rr={Fmt.N3(r / 255.0)}:gg={Fmt.N3(g / 255.0)}:bb={Fmt.N3(b / 255.0)}";
        }

        public static string Normalize(string hex)
        {
            return TryParse(hex, out var r, out var g, out var b) ? $"#{r:X2}{g:X2}{b:X2}" : null;
        }
    }

    public static class Hash
    {
        public static string Of(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return string.Concat(bytes.Take(10).Select(x => x.ToString("x2")));
        }
    }
}
