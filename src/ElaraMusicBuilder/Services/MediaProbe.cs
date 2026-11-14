using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElaraMusicBuilder.Services
{
    public class VideoInfo
    {
        public double Duration;
        public int Width;
        public int Height;
        public bool HasVideo;
        public bool HasAudio;
    }

    public static class MediaProbe
    {
        public const int SampleRate = 48000;

        /// <summary>
        /// Точная длительность аудио: файл декодируется, пересэмплируется в 48 кГц и считается количество сэмплов.
        /// Метаданные MP3 часто содержат неточную длительность, поэтому они не используются для тайм-кодов.
        /// </summary>
        public static async Task<double> MeasureAudioSecondsAsync(string path, CancellationToken ct)
        {
            var args = new List<string>
            {
                "-hide_banner", "-nostats", "-i", path, "-map", "0:a:0", "-vn",
                "-af", $"aresample={SampleRate},astats=measure_perchannel=none:measure_overall=Number_of_samples",
                "-f", "null", "-"
            };
            var r = await FfmpegService.RunAsync(args, null, null, ct);
            if (r.Cancelled) throw new OperationCanceledException();
            var m = Regex.Matches(r.StdErr ?? "", @"Number of samples:\s*(\d+)");
            if (m.Count > 0)
            {
                long samples = long.Parse(m[m.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
                if (samples > 0) return samples / (double)SampleRate;
            }
            // Запасной путь — длительность из контейнера
            var d = ProbeDuration(path);
            if (d > 0) return d;
            throw new InvalidOperationException("Не удалось прочитать аудиофайл: " + Path.GetFileName(path) + Environment.NewLine + r.ErrorTail);
        }

        public static double ProbeDuration(string path)
        {
            try
            {
                var outp = FfmpegService.RunCapture(FfmpegService.FfprobePath,
                    new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path }, null, 60000);
                var line = outp.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
                return double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            }
            catch { return 0; }
        }

        public static string AudioFormatLabel(string path)
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            return string.IsNullOrEmpty(ext) ? "?" : ext;
        }

        public static VideoInfo ProbeVideo(string path)
        {
            var info = new VideoInfo();
            try
            {
                var json = FfmpegService.RunCapture(FfmpegService.FfprobePath,
                    new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, null, 60000);
                int start = json.IndexOf('{');
                if (start < 0) return info;
                using var doc = JsonDocument.Parse(json.Substring(start));
                var root = doc.RootElement;
                if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var dur))
                    double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out info.Duration);
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        var type = s.TryGetProperty("codec_type", out var ctp) ? ctp.GetString() : "";
                        if (type == "video" && !info.HasVideo)
                        {
                            info.HasVideo = true;
                            if (s.TryGetProperty("width", out var w)) info.Width = w.GetInt32();
                            if (s.TryGetProperty("height", out var h)) info.Height = h.GetInt32();
                        }
                        if (type == "audio") info.HasAudio = true;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("ffprobe: " + ex.Message); }
            return info;
        }

        /// <summary>Проверка готового видео: файл существует, есть видео и звук, длительность совпадает с ожидаемой.</summary>
        public static string ValidateExport(string path, double expectedSeconds)
        {
            if (!File.Exists(path)) return "Файл видео не был создан.";
            if (new FileInfo(path).Length < 1024) return "Файл видео пустой.";
            var vi = ProbeVideo(path);
            if (!vi.HasVideo) return "В файле нет видеодорожки.";
            if (!vi.HasAudio) return "В файле нет звуковой дорожки.";
            double tol = Math.Max(1.0, expectedSeconds * 0.002);
            if (Math.Abs(vi.Duration - expectedSeconds) > tol)
                return $"Длительность видео ({Fmt.Time(vi.Duration)}) не совпадает с ожидаемой ({Fmt.Time(expectedSeconds)}).";
            return null;
        }
    }
}
