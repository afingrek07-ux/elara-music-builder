using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    /// <summary>
    /// Склеивает треки в один непрерывный альбом (WAV 48 кГц, 16 бит).
    /// Оригинальные файлы никогда не изменяются — FFmpeg только читает их.
    /// </summary>
    public static class AudioAlbumBuilder
    {
        public class Result
        {
            public string WavPath;
            public Timeline Timeline;
            public double ActualDuration;
            public string Warning;
        }

        public static string CacheKey(ProjectModel p)
        {
            var sb = new StringBuilder("album-v2|");
            foreach (var t in p.Tracks) sb.Append(t.FilePath).Append('|').Append(t.FileSize).Append('|').Append(t.FileTimeUtc.Ticks).Append('|').Append(Fmt.N(t.Duration)).Append(';');
            var a = p.Audio;
            sb.Append(a.Transition).Append(Fmt.N(a.GapSeconds)).Append(Fmt.N(a.CrossfadeSeconds)).Append(Fmt.N(a.TrackFadeSeconds))
              .Append(a.AlbumFadeIn).Append(Fmt.N(a.AlbumFadeInSeconds)).Append(a.AlbumFadeOut).Append(Fmt.N(a.AlbumFadeOutSeconds)).Append(a.Loudness);
            return Hash.Of(sb.ToString());
        }

        public static string CachedPath(ProjectModel p) => Path.Combine(AppPaths.Cache, "album_" + CacheKey(p) + ".wav");

        public static bool IsCached(ProjectModel p) => File.Exists(CachedPath(p));

        /// <summary>Строит граф фильтров склейки. Возвращает метку выхода "[mix]".</summary>
        public static string BuildMixGraph(IList<TrackItem> tracks, AudioSettings a, Timeline tl)
        {
            var g = new StringBuilder();
            int n = tracks.Count;
            for (int i = 0; i < n; i++)
            {
                g.Append($"[{i}:a:0]aresample={MediaProbe.SampleRate},aformat=sample_fmts=fltp:channel_layouts=stereo");
                double d = tl.Entries[i].Duration;
                if (a.Transition == TransitionMode.FadeInOut && a.TrackFadeSeconds > 0)
                {
                    double f = Math.Min(a.TrackFadeSeconds, d / 3);
                    g.Append($",afade=t=in:st=0:d={Fmt.N(f)},afade=t=out:st={Fmt.N(Math.Max(0, d - f))}:d={Fmt.N(f)}");
                }
                // Точная обрезка по измеренной длине: гарантирует совпадение тайм-кодов
                g.Append($",atrim=end_sample={(long)Math.Round(d * MediaProbe.SampleRate)}");
                if (a.Transition == TransitionMode.Silence && i < n - 1 && a.GapSeconds > 0)
                    g.Append($",apad=pad_len={(long)Math.Round(a.GapSeconds * MediaProbe.SampleRate)}");
                g.Append($"[a{i}];");
            }

            if (n == 1)
            {
                g.Append("[a0]anull[cat];");
            }
            else if (a.Transition == TransitionMode.Crossfade)
            {
                string prev = "a0";
                for (int i = 1; i < n; i++)
                {
                    double xf = tl.Entries[i].OverlapWithPrevious;
                    string outLabel = i == n - 1 ? "cat" : "x" + i;
                    if (xf > 0.01)
                        g.Append($"[{prev}][a{i}]acrossfade=d={Fmt.N(xf)}:c1=tri:c2=tri[{outLabel}];");
                    else
                        g.Append($"[{prev}][a{i}]concat=n=2:v=0:a=1[{outLabel}];");
                    prev = outLabel;
                }
            }
            else
            {
                for (int i = 0; i < n; i++) g.Append($"[a{i}]");
                g.Append($"concat=n={n}:v=0:a=1[cat];");
            }

            g.Append("[cat]anull");
            if (a.AlbumFadeIn && a.AlbumFadeInSeconds > 0)
                g.Append($",afade=t=in:st=0:d={Fmt.N(Math.Min(a.AlbumFadeInSeconds, tl.Total / 4))}");
            if (a.AlbumFadeOut && a.AlbumFadeOutSeconds > 0)
            {
                double fo = Math.Min(a.AlbumFadeOutSeconds, tl.Total / 4);
                g.Append($",afade=t=out:st={Fmt.N(Math.Max(0, tl.Total - fo))}:d={Fmt.N(fo)}");
            }
            g.Append("[mix]");
            return g.ToString();
        }

        private static (double I, double TP, double LRA) Target(LoudnessPreset p) => p switch
        {
            LoudnessPreset.Soft => (-16, -1.5, 9),
            _ => (-14, -1.0, 11)
        };

        public static async Task<Result> BuildAsync(ProjectModel p, IProgress<(double frac, string msg)> progress, CancellationToken ct)
        {
            if (p.Tracks.Count == 0) throw new InvalidOperationException("Добавьте хотя бы один трек.");
            var missing = p.Tracks.Where(t => !File.Exists(t.FilePath)).ToList();
            if (missing.Count > 0)
                throw new FileNotFoundException("Не найдены исходные файлы:" + Environment.NewLine + string.Join(Environment.NewLine, missing.Select(m => "• " + m.FilePath)));
            if (p.Tracks.Any(t => t.Duration <= 0))
                throw new InvalidOperationException("Длительность некоторых треков ещё измеряется. Подождите пару секунд.");

            var tl = Timeline.Compute(p.Tracks, p.Audio);
            var outPath = CachedPath(p);
            if (File.Exists(outPath))
            {
                progress?.Report((1, "Альбом уже собран"));
                return new Result { WavPath = outPath, Timeline = tl, ActualDuration = MediaProbe.ProbeDuration(outPath) };
            }

            // Свободное место: WAV 48 кГц / 16 бит / стерео ≈ 11.5 МБ в минуту
            long need = (long)(tl.Total * MediaProbe.SampleRate * 4 * 1.1) + 50_000_000;
            CheckDisk(AppPaths.Cache, need);

            var work = AppPaths.NewWorkDir("album");
            try
            {
                var graph = BuildMixGraph(p.Tracks, p.Audio, tl);
                var inputs = new List<string>();
                foreach (var t in p.Tracks) { inputs.Add("-i"); inputs.Add(t.FilePath); }

                string loud = "";
                if (p.Audio.Loudness != LoudnessPreset.None)
                {
                    var tg = Target(p.Audio.Loudness);
                    progress?.Report((0.02, "Анализ громкости (проход 1 из 2)…"));
                    var script1 = Path.Combine(work, "pass1.txt");
                    File.WriteAllText(script1, graph + $";[mix]loudnorm=I={Fmt.N(tg.I)}:TP={Fmt.N(tg.TP)}:LRA={Fmt.N(tg.LRA)}:print_format=json[out]", new UTF8Encoding(false));
                    var a1 = new List<string> { "-hide_banner", "-y" };
                    a1.AddRange(inputs);
                    a1.AddRange(FfmpegService.FilterScriptArgs(script1));
                    a1.AddRange(new[] { "-map", "[out]", "-f", "null", "-" });
                    var r1 = await FfmpegService.RunAsync(a1, work, s => progress?.Report((0.02 + 0.45 * Math.Min(1, s / Math.Max(1, tl.Total)), "Анализ громкости (проход 1 из 2)…")), ct);
                    if (r1.Cancelled) throw new OperationCanceledException();
                    if (!r1.Ok) throw new InvalidOperationException("Ошибка анализа громкости." + Environment.NewLine + r1.ErrorTail);
                    var m = ParseLoudnorm(r1.StdErr);
                    if (m != null)
                    {
                        loud = $",loudnorm=I={Fmt.N(tg.I)}:TP={Fmt.N(tg.TP)}:LRA={Fmt.N(tg.LRA)}" +
                               $":measured_I={m["input_i"]}:measured_TP={m["input_tp"]}:measured_LRA={m["input_lra"]}" +
                               $":measured_thresh={m["input_thresh"]}:offset={m["target_offset"]}:linear=true,aresample={MediaProbe.SampleRate}";
                    }
                    else
                    {
                        Log.Warn("Не удалось прочитать результаты loudnorm, используется однопроходный режим.");
                        loud = $",loudnorm=I={Fmt.N(tg.I)}:TP={Fmt.N(tg.TP)}:LRA={Fmt.N(tg.LRA)},aresample={MediaProbe.SampleRate}";
                    }
                }
                else
                {
                    // Без обработки: только защитный лимитер от перегруза при кроссфейде
                    loud = ",alimiter=limit=0.98:attack=5:release=50:level=0";
                }

                double p0 = p.Audio.Loudness != LoudnessPreset.None ? 0.47 : 0.02;
                progress?.Report((p0, "Сборка альбома…"));
                var script2 = Path.Combine(work, "pass2.txt");
                File.WriteAllText(script2, graph + $";[mix]anull{loud}[out]", new UTF8Encoding(false));
                var tmp = outPath + ".part.wav";
                var a2 = new List<string> { "-hide_banner", "-y" };
                a2.AddRange(inputs);
                a2.AddRange(FfmpegService.FilterScriptArgs(script2));
                a2.AddRange(new[] { "-map", "[out]", "-ar", Fmt.I(MediaProbe.SampleRate), "-ac", "2", "-c:a", "pcm_s16le", "-rf64", "auto", tmp });
                var r2 = await FfmpegService.RunAsync(a2, work, s => progress?.Report((p0 + (0.98 - p0) * Math.Min(1, s / Math.Max(1, tl.Total)), "Сборка альбома…")), ct);
                if (r2.Cancelled) { TryDelete(tmp); throw new OperationCanceledException(); }
                if (!r2.Ok) { TryDelete(tmp); throw new InvalidOperationException("Ошибка сборки альбома." + Environment.NewLine + r2.ErrorTail); }

                double actual = MediaProbe.ProbeDuration(tmp);
                var res = new Result { WavPath = outPath, Timeline = tl, ActualDuration = actual };
                if (Math.Abs(actual - tl.Total) > 0.5)
                {
                    res.Warning = $"Длительность альбома ({Fmt.Time(actual)}) отличается от расчётной ({Fmt.Time(tl.Total)}).";
                    Log.Warn(res.Warning);
                }
                File.Move(tmp, outPath, true);
                progress?.Report((1, "Альбом готов"));
                return res;
            }
            finally
            {
                AppPaths.TryDeleteDir(work);
            }
        }

        /// <summary>Экспорт готового альбома в MP3 320 кбит/с или WAV.</summary>
        public static async Task ExportAlbumAsync(string albumWav, string target, double total, IProgress<(double, string)> progress, CancellationToken ct)
        {
            var args = new List<string> { "-hide_banner", "-y", "-i", albumWav };
            if (target.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "320k" });
            else
                args.AddRange(new[] { "-c:a", "pcm_s16le", "-rf64", "auto" });
            var tmp = target + ".part" + Path.GetExtension(target);
            args.Add(tmp);
            var r = await FfmpegService.RunAsync(args, null, s => progress?.Report((Math.Min(1, s / Math.Max(1, total)), "Экспорт альбома…")), ct);
            if (r.Cancelled) { TryDelete(tmp); throw new OperationCanceledException(); }
            if (!r.Ok) { TryDelete(tmp); throw new InvalidOperationException("Не удалось сохранить альбом." + Environment.NewLine + r.ErrorTail); }
            File.Move(tmp, target, true);
        }

        private static Dictionary<string, string> ParseLoudnorm(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return null;
            int end = stderr.LastIndexOf('}');
            int start = end > 0 ? stderr.LastIndexOf('{', end) : -1;
            if (start < 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(stderr.Substring(start, end - start + 1));
                var d = new Dictionary<string, string>();
                foreach (var key in new[] { "input_i", "input_tp", "input_lra", "input_thresh", "target_offset" })
                {
                    var v = doc.RootElement.GetProperty(key).GetString();
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) || double.IsInfinity(dv)) return null;
                    d[key] = Fmt.N(dv);
                }
                return d;
            }
            catch { return null; }
        }

        public static void CheckDisk(string folder, long bytesNeeded)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                var di = new DriveInfo(root);
                if (di.AvailableFreeSpace < bytesNeeded)
                    throw new IOException($"Недостаточно места на диске {root}. Нужно примерно {Fmt.Bytes(bytesNeeded)}, свободно {Fmt.Bytes(di.AvailableFreeSpace)}.");
            }
            catch (IOException) { throw; }
            catch (Exception ex) { Log.Warn("Проверка места на диске: " + ex.Message); }
        }

        public static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }
}
