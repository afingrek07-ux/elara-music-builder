using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    /// <summary>
    /// Готовит видеофон к бесконечному повторению.
    /// Обычное повторение использует исходный файл; остальные режимы создают один «цикл» в кэше,
    /// который затем повторяется до конца музыки.
    /// </summary>
    public static class BackgroundPreparer
    {
        public class Prepared
        {
            public string Path;
            public bool IsImage;
            public double LoopDuration; // для видео
            public string Note;
        }

        public static async Task<Prepared> PrepareAsync(BackgroundSettings bg, int w, int h, int fps, IProgress<(double, string)> progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(bg.FilePath) || !File.Exists(bg.FilePath))
                return null;
            if (bg.IsImage) return new Prepared { Path = bg.FilePath, IsImage = true };

            var info = MediaProbe.ProbeVideo(bg.FilePath);
            if (!info.HasVideo || info.Duration <= 0.2)
                throw new InvalidOperationException("Не удалось прочитать видео фона: " + System.IO.Path.GetFileName(bg.FilePath));

            if (bg.LoopMode == BackgroundLoopMode.Normal)
                return new Prepared { Path = bg.FilePath, LoopDuration = info.Duration };

            var fi = new FileInfo(bg.FilePath);
            string key = Hash.Of($"loop-v1|{bg.FilePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{bg.LoopMode}|{w}x{h}|{fps}");
            string outPath = System.IO.Path.Combine(AppPaths.Cache, "bgloop_" + key + ".mp4");
            if (File.Exists(outPath))
                return new Prepared { Path = outPath, LoopDuration = MediaProbe.ProbeDuration(outPath) };

            string scale = $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,fps={fps},format=yuv420p";
            string graph;
            string note = null;
            double L = info.Duration;

            switch (bg.LoopMode)
            {
                case BackgroundLoopMode.PingPong:
                {
                    // reverse хранит кадры в памяти, поэтому длинные ролики ограничиваются 20 секундами
                    double len = Math.Min(L, 20);
                    if (L > 20) note = "Для режима «Вперёд-назад» использованы первые 20 секунд видео.";
                    graph = $"[0:v]trim=end={Fmt.N(len)},setpts=PTS-STARTPTS,{scale},split[f][r];[r]reverse[rv];[f][rv]concat=n=2:v=1:a=0[out]";
                    break;
                }
                case BackgroundLoopMode.Crossfade:
                {
                    double d = Math.Min(1.5, L / 4);
                    graph = SpliceGraph(scale, L, d);
                    break;
                }
                default: // Smart
                {
                    progress?.Report((0.1, "Поиск лучшей точки склейки…"));
                    double cut = await Task.Run(() => FindBestLoopPoint(bg.FilePath, L), ct);
                    double d = Math.Min(0.6, cut / 4);
                    note = $"Умное зацикливание: найдена точка склейки на {Fmt.N3(cut)} с (плавный переход {Fmt.N3(d)} с).";
                    graph = SpliceGraph(scale, cut, d);
                    break;
                }
            }

            var work = AppPaths.NewWorkDir("bg");
            try
            {
                var script = System.IO.Path.Combine(work, "loop.txt");
                File.WriteAllText(script, graph);
                var args = new List<string> { "-hide_banner", "-y", "-i", bg.FilePath };
                args.AddRange(FfmpegService.FilterScriptArgs(script));
                var tmp = outPath + ".part.mp4";
                args.AddRange(new[] { "-map", "[out]", "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "16", "-pix_fmt", "yuv420p", tmp });
                progress?.Report((0.3, "Подготовка цикла видео…"));
                var r = await FfmpegService.RunAsync(args, work, s => progress?.Report((0.3 + 0.7 * Math.Min(1, s / Math.Max(1, L)), "Подготовка цикла видео…")), ct);
                if (r.Cancelled) { AudioAlbumBuilder.TryDelete(tmp); throw new OperationCanceledException(); }
                if (!r.Ok) { AudioAlbumBuilder.TryDelete(tmp); throw new InvalidOperationException("Не удалось подготовить зацикленный фон." + Environment.NewLine + r.ErrorTail); }
                File.Move(tmp, outPath, true);
                if (note != null) Log.Info(note);
                return new Prepared { Path = outPath, LoopDuration = MediaProbe.ProbeDuration(outPath), Note = note };
            }
            finally { AppPaths.TryDeleteDir(work); }
        }

        /// <summary>
        /// Плавная склейка: берём отрезок [d, len], а его последние d секунд растворяем в начале [0, d].
        /// Конец цикла совпадает с кадром на отметке d, с которого цикл начинается — шва нет.
        /// </summary>
        private static string SpliceGraph(string scale, double len, double d)
        {
            double offset = Math.Max(0.01, (len - d) - d);
            return $"[0:v]{scale},settb=AVTB,split[a][b];" +
                   $"[a]trim=start={Fmt.N(d)}:end={Fmt.N(len)},setpts=PTS-STARTPTS[p1];" +
                   $"[b]trim=start=0:end={Fmt.N(d)},setpts=PTS-STARTPTS[p2];" +
                   $"[p1][p2]xfade=transition=fade:duration={Fmt.N(d)}:offset={Fmt.N(offset)}[out]";
        }

        /// <summary>
        /// Умное зацикливание: кадры уменьшаются до 32×18 в оттенках серого (10 кадров/с),
        /// затем ищется кадр во второй половине ролика, наиболее похожий на первый кадр (минимальная MSE).
        /// Это настоящий анализ, но идеальный бесшовный цикл не гарантирован — зависит от самого видео.
        /// </summary>
        public static double FindBestLoopPoint(string path, double duration)
        {
            const int W = 32, H = 18, FPS = 10, FS = W * H;
            var raw = FfmpegService.RunBinary(new[] { "-hide_banner", "-v", "error", "-i", path, "-an", "-vf", $"fps={FPS},scale={W}:{H},format=gray", "-f", "rawvideo", "-" });
            int frames = raw.Length / FS;
            if (frames < 6) return duration;
            // эталон — среднее первых двух кадров
            var refFrame = new double[FS];
            for (int i = 0; i < FS; i++) refFrame[i] = (raw[i] + raw[FS + i]) / 2.0;
            int from = Math.Max(2, (int)(frames * 0.5));
            int best = frames - 1;
            double bestScore = double.MaxValue;
            for (int f = from; f < frames; f++)
            {
                double sum = 0;
                int off = f * FS;
                for (int i = 0; i < FS; i++) { double dlt = raw[off + i] - refFrame[i]; sum += dlt * dlt; }
                // лёгкое предпочтение более длинным циклам
                double score = sum / FS * (1.0 + 0.15 * (1.0 - f / (double)frames));
                if (score < bestScore) { bestScore = score; best = f; }
            }
            double t = best / (double)FPS;
            return Math.Max(1.0, Math.Min(duration, t));
        }
    }
}
