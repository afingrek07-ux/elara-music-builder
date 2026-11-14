using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    public class ExportOutcome
    {
        public string OutputPath;
        public double Duration;
        public string EncoderUsed;
        public Timeline Timeline;
        public List<string> Notes = new List<string>();
    }

    /// <summary>Создание итогового MP4 (H.264 + AAC), тестового 20-секундного видео и кадров предпросмотра.</summary>
    public static class VideoExporter
    {
        public static void EnsureFfmpeg()
        {
            if (!FfmpegService.IsAvailable)
                throw new FileNotFoundException("Не найден встроенный модуль FFmpeg. Переустановите программу — модуль входит в установщик.");
        }

        private static long EstimateBytes(int height, double seconds)
        {
            double mbps = height switch { 720 => 5, 1440 => 18, 2160 => 40, _ => 10 };
            return (long)((mbps * 1_000_000 + 320_000) / 8 * seconds * 1.25) + 100_000_000;
        }

        public static async Task<ExportOutcome> ExportAsync(ProjectModel p, bool isTest, double testStart,
            IProgress<(double frac, string msg)> progress, CancellationToken ct)
        {
            EnsureFfmpeg();
            var ex = p.Export;
            int W = ex.Width, H = ex.Height, F = ex.Fps;
            var outcome = new ExportOutcome();

            // 1. Аудиоальбом
            var album = await AudioAlbumBuilder.BuildAsync(p,
                new Progress<(double, string)>(x => progress?.Report((x.Item1 * 0.15, x.Item2))), ct);
            var tl = album.Timeline;
            outcome.Timeline = tl;
            if (album.Warning != null) outcome.Notes.Add(album.Warning);

            // 2. Фон
            progress?.Report((0.15, "Подготовка фона…"));
            var bg = await BackgroundPreparer.PrepareAsync(p.Background, W, H, F,
                new Progress<(double, string)>(x => progress?.Report((0.15 + x.Item1 * 0.05, x.Item2))), ct);
            if (bg?.Note != null) outcome.Notes.Add(bg.Note);

            double start = isTest ? Math.Max(0, Math.Min(testStart, Math.Max(0, tl.Total - 1))) : 0;
            double duration = isTest ? Math.Min(20, tl.Total - start) : tl.Total;
            if (duration <= 0.5) throw new InvalidOperationException("Слишком короткий фрагмент для видео.");

            // 3. Папка и имя файла
            string folder = string.IsNullOrWhiteSpace(ex.OutputFolder) ? AppPaths.DefaultOutput : ex.OutputFolder;
            Directory.CreateDirectory(folder);
            string baseName = SafeFileName(string.IsNullOrWhiteSpace(ex.FileName) ? "Elara_Album" : ex.FileName);
            string outPath = isTest
                ? Path.Combine(folder, baseName + "_тест_20сек.mp4")
                : UniquePath(Path.Combine(folder, baseName + ".mp4"));
            if (!isTest) AudioAlbumBuilder.CheckDisk(folder, EstimateBytes(H, duration));

            string logo = p.Overlay.ShowLogo && !string.IsNullOrEmpty(p.Overlay.LogoPath) && File.Exists(p.Overlay.LogoPath) ? p.Overlay.LogoPath : null;

            var work = AppPaths.NewWorkDir(isTest ? "test" : "export");
            try
            {
                var ctx = new RenderContext
                {
                    Project = p, Timeline = tl, W = W, H = H, Fps = F, Offset = start, WorkDir = work,
                    BackgroundIsImage = bg?.IsImage ?? false, HasLogo = logo != null
                };
                var graph = FilterGraphBuilder.Build(ctx);
                var script = Path.Combine(work, "graph.txt");
                File.WriteAllText(script, graph, new UTF8Encoding(false));

                string gpu = ex.UseGpu ? await Task.Run(() => FfmpegService.DetectGpuEncoder(), ct) : null;
                string tmp = outPath + ".part.mp4";

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    string enc = attempt == 0 ? gpu : null;
                    if (attempt == 1 && gpu == null) break;
                    var args = BuildArgs(bg, ctx, album.WavPath, logo, script, start, duration, enc, ex.Quality, tmp);
                    progress?.Report((0.2, enc == null ? "Создание видео…" : $"Создание видео (видеокарта: {FfmpegService.EncoderDisplayName(enc)})…"));
                    var r = await FfmpegService.RunAsync(args, work,
                        s => progress?.Report((0.2 + 0.78 * Math.Min(1, s / duration), $"Создание видео… {Fmt.Time(s)} из {Fmt.Time(duration)}")), ct);
                    if (r.Cancelled) { AudioAlbumBuilder.TryDelete(tmp); throw new OperationCanceledException(); }
                    if (r.Ok) { outcome.EncoderUsed = FfmpegService.EncoderDisplayName(enc); break; }
                    AudioAlbumBuilder.TryDelete(tmp);
                    if (enc != null && attempt == 0)
                    {
                        Log.Warn("Аппаратное кодирование не удалось, переключаюсь на процессор.");
                        outcome.Notes.Add("Видеокарта не справилась с кодированием — видео создано процессором.");
                        continue;
                    }
                    throw new InvalidOperationException("FFmpeg не смог создать видео." + Environment.NewLine + r.ErrorTail);
                }

                // 4. Проверка результата — успех сообщается только после проверки готового файла
                progress?.Report((0.99, "Проверка готового файла…"));
                var err = MediaProbe.ValidateExport(tmp, duration);
                if (err != null) { AudioAlbumBuilder.TryDelete(tmp); throw new InvalidOperationException("Проверка видео не пройдена: " + err); }
                File.Move(tmp, outPath, true);
                outcome.OutputPath = outPath;
                outcome.Duration = duration;
                progress?.Report((1, "Готово"));
                Log.Info("Видео готово: " + outPath);
                return outcome;
            }
            finally { AppPaths.TryDeleteDir(work); }
        }

        private static List<string> BuildArgs(BackgroundPreparer.Prepared bg, RenderContext c, string albumWav, string logo,
            string script, double start, double duration, string encoder, ExportQuality q, string output)
        {
            var a = new List<string> { "-hide_banner", "-y" };
            // Вход 0: фон
            if (bg == null)
                a.AddRange(new[] { "-f", "lavfi", "-i", $"color=c=0x15131B:s={c.W}x{c.H}:r={c.Fps}" });
            else if (bg.IsImage)
                a.AddRange(new[] { "-loop", "1", "-framerate", Fmt.I(c.Fps), "-i", bg.Path });
            else
            {
                if (start > 0 && bg.LoopDuration > 0) a.AddRange(new[] { "-ss", Fmt.N3(start % bg.LoopDuration) });
                a.AddRange(new[] { "-stream_loop", "-1", "-i", bg.Path });
            }
            // Вход 1: звук альбома
            if (start > 0) a.AddRange(new[] { "-ss", Fmt.N3(start) });
            a.AddRange(new[] { "-t", Fmt.N3(duration), "-i", albumWav });
            // Вход 2: логотип
            if (logo != null) a.AddRange(new[] { "-i", logo });

            a.AddRange(FfmpegService.FilterScriptArgs(script));
            a.AddRange(new[] { "-map", "[vout]", "-map", "1:a", "-t", Fmt.N3(duration), "-r", Fmt.I(c.Fps) });
            a.AddRange(EncoderArgs(encoder, q, c.Fps));
            a.AddRange(new[] { "-c:a", "aac", "-b:a", "320k", "-ar", "48000", "-movflags", "+faststart", output });
            return a;
        }

        public static IEnumerable<string> EncoderArgs(string encoder, ExportQuality q, int fps)
        {
            string gop = Fmt.I(fps * 2);
            switch (encoder)
            {
                case "h264_nvenc":
                    return new[] { "-c:v", "h264_nvenc", "-preset", q == ExportQuality.Fast ? "p3" : (q == ExportQuality.High ? "p6" : "p5"), "-rc", "vbr", "-cq", q == ExportQuality.High ? "19" : "21", "-b:v", "0", "-g", gop, "-pix_fmt", "yuv420p" };
                case "h264_qsv":
                    return new[] { "-c:v", "h264_qsv", "-preset", q == ExportQuality.Fast ? "faster" : "medium", "-global_quality", q == ExportQuality.High ? "20" : "23", "-g", gop, "-pix_fmt", "nv12" };
                case "h264_amf":
                    return new[] { "-c:v", "h264_amf", "-quality", q == ExportQuality.Fast ? "speed" : "quality", "-rc", "cqp", "-qp_i", q == ExportQuality.High ? "19" : "21", "-qp_p", q == ExportQuality.High ? "21" : "23", "-g", gop, "-pix_fmt", "yuv420p" };
                default:
                    string preset = q switch { ExportQuality.Fast => "veryfast", ExportQuality.High => "medium", _ => "faster" };
                    return new[] { "-c:v", "libx264", "-preset", preset, "-crf", q == ExportQuality.High ? "18" : "20", "-g", gop, "-pix_fmt", "yuv420p", "-profile:v", "high" };
            }
        }

        /// <summary>
        /// Настоящий кадр предпросмотра: тот же граф фильтров, что и при экспорте, но в 1280×720.
        /// Рендерится 1 секунда (визуализатору нужен звук), сохраняется последний кадр.
        /// </summary>
        public static async Task<string> RenderPreviewFrameAsync(ProjectModel p, Timeline tl, double t, CancellationToken ct)
        {
            EnsureFfmpeg();
            const int W = 1280, H = 720, F = 30;
            double seg = 1.0;
            double start = Math.Max(0, t - seg);
            var work = AppPaths.NewWorkDir("preview");
            string outPng = Path.Combine(work, "frame.png");
            string logo = p.Overlay.ShowLogo && !string.IsNullOrEmpty(p.Overlay.LogoPath) && File.Exists(p.Overlay.LogoPath) ? p.Overlay.LogoPath : null;
            var bgPath = p.Background.FilePath;
            bool hasBg = !string.IsNullOrEmpty(bgPath) && File.Exists(bgPath);
            bool isImage = hasBg && p.Background.IsImage;

            var ctx = new RenderContext { Project = p, Timeline = tl, W = W, H = H, Fps = F, Offset = start, WorkDir = work, BackgroundIsImage = isImage, HasLogo = logo != null };
            var script = Path.Combine(work, "graph.txt");
            File.WriteAllText(script, FilterGraphBuilder.Build(ctx), new UTF8Encoding(false));

            var a = new List<string> { "-hide_banner", "-y" };
            if (!hasBg) a.AddRange(new[] { "-f", "lavfi", "-i", $"color=c=0x15131B:s={W}x{H}:r={F}" });
            else if (isImage) a.AddRange(new[] { "-loop", "1", "-framerate", Fmt.I(F), "-i", bgPath });
            else
            {
                double len = MediaProbe.ProbeDuration(bgPath);
                if (len > 0 && start > 0) a.AddRange(new[] { "-ss", Fmt.N3(start % len) });
                a.AddRange(new[] { "-stream_loop", "-1", "-i", bgPath });
            }

            // Звук для визуализатора: готовый альбом из кэша или нужный трек
            string albumCached = AudioAlbumBuilder.IsCached(p) ? AudioAlbumBuilder.CachedPath(p) : null;
            if (albumCached != null)
                a.AddRange(new[] { "-ss", Fmt.N3(start), "-t", "1.5", "-i", albumCached });
            else if (tl.Entries.Count > 0 && p.Tracks.Count == tl.Entries.Count)
            {
                int i = tl.IndexAt(start);
                var tr = p.Tracks[i];
                double local = Math.Max(0, start - tl.Entries[i].Start);
                if (File.Exists(tr.FilePath)) a.AddRange(new[] { "-ss", Fmt.N3(local), "-t", "1.5", "-i", tr.FilePath });
                else a.AddRange(new[] { "-f", "lavfi", "-t", "1.5", "-i", "anullsrc=r=48000:cl=stereo" });
            }
            else a.AddRange(new[] { "-f", "lavfi", "-t", "1.5", "-i", "anullsrc=r=48000:cl=stereo" });

            if (logo != null) a.AddRange(new[] { "-i", logo });
            a.AddRange(FfmpegService.FilterScriptArgs(script));
            a.AddRange(new[] { "-map", "[vout]", "-an", "-t", Fmt.N3(seg), "-update", "1", outPng });

            var r = await FfmpegService.RunAsync(a, work, null, ct);
            if (r.Cancelled) throw new OperationCanceledException();
            if (!r.Ok || !File.Exists(outPng)) throw new InvalidOperationException("Не удалось построить кадр предпросмотра." + Environment.NewLine + r.ErrorTail);
            return outPng;
        }

        public static string SafeFileName(string name)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name.Trim().TrimEnd('.');
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
            return path;
        }
    }
}
