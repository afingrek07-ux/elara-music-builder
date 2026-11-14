using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElaraMusicBuilder.Models;
using ElaraMusicBuilder.Services;

namespace EngineTests
{
    /// <summary>
    /// Функциональные тесты движка Elara Music Builder с настоящим FFmpeg.
    /// Код возврата 0 — все тесты пройдены. Отчёт пишется в test-report.md.
    /// </summary>
    internal static class Program
    {
        private static readonly List<(string name, bool ok, string info)> Results = new List<(string, bool, string)>();
        private static string Dir;

        [STAThread]
        private static int Main()
        {
            Dir = Path.Combine(Directory.GetCurrentDirectory(), "test-output");
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("FFmpeg: " + FfmpegService.FfmpegPath);
            Console.WriteLine(FfmpegService.VersionText());

            Run("FFmpeg найден", () => { if (!FfmpegService.IsAvailable) throw new Exception("нет ffmpeg/ffprobe"); return "ok"; });

            string t1 = Tone("t1.mp3", 220, 12), t2 = Tone("t2.wav", 330, 9), t3 = Tone("t3.flac", 262, 10);
            string bgImg = Path.Combine(Dir, "bg.png");
            Ff("-f", "lavfi", "-i", "gradients=s=1920x1080:c0=0x3B1A3A:c1=0x1B2A44:duration=1", "-frames:v", "1", bgImg);
            string bgVid = Path.Combine(Dir, "clip.mp4");
            Ff("-f", "lavfi", "-i", "testsrc2=s=1280x720:d=4:r=30", "-pix_fmt", "yuv420p", bgVid);
            string logo = Path.Combine(Dir, "logo.png");
            Ff("-f", "lavfi", "-i", "color=c=0x7868E6:s=200x200", "-frames:v", "1", logo);

            ProjectModel NewProject()
            {
                var p = new ProjectModel();
                foreach (var f in new[] { t1, t2, t3 })
                {
                    var fi = new FileInfo(f);
                    p.Tracks.Add(new TrackItem { FilePath = f, Title = "Песня " + Path.GetFileNameWithoutExtension(f) + " — «тест»", FileSize = fi.Length, FileTimeUtc = fi.LastWriteTimeUtc, Format = "X" });
                }
                foreach (var t in p.Tracks) t.Duration = MediaProbe.MeasureAudioSecondsAsync(t.FilePath, CancellationToken.None).GetAwaiter().GetResult();
                p.Export.Height = 720; p.Export.Fps = 30; p.Export.OutputFolder = Path.Combine(Dir, "out"); p.Export.UseGpu = false; p.Export.Quality = ExportQuality.Fast;
                p.Background.FilePath = bgImg;
                p.Overlay.LogoPath = logo; p.Overlay.ShowCustomText = true; p.Overlay.CustomText = "Subscribe ✦ Подписывайтесь";
                return p;
            }

            // 1. Импорт MP3/WAV/FLAC и точные длительности
            ProjectModel baseP = null;
            Run("Импорт MP3, WAV, FLAC и точные длительности", () =>
            {
                baseP = NewProject();
                var d = baseP.Tracks.Select(t => t.Duration).ToArray();
                Expect(Math.Abs(d[0] - 12) < 0.05 && Math.Abs(d[1] - 9) < 0.01 && Math.Abs(d[2] - 10) < 0.01, "длительности: " + string.Join(", ", d));
                return string.Join(" / ", d.Select(Fmt.N3));
            });

            // 2. Сортировка и порядок
            Run("Сортировка треков по имени", () =>
            {
                var p = NewProject();
                p.Tracks.Move(0, 2);
                var sorted = p.Tracks.OrderBy(t => Path.GetFileName(t.FilePath)).Select(t => Path.GetFileName(t.FilePath)).ToArray();
                Expect(sorted[0] == "t1.mp3" && sorted[2] == "t3.flac", string.Join(",", sorted));
                return string.Join(", ", sorted);
            });

            // 3–4. Тайм-коды и склейка для всех режимов переходов
            foreach (var mode in new[] { TransitionMode.None, TransitionMode.Silence, TransitionMode.FadeInOut, TransitionMode.Crossfade })
            {
                Run($"Склейка альбома и тайм-коды: {mode}", () =>
                {
                    var p = NewProject();
                    p.Audio.Transition = mode; p.Audio.GapSeconds = 2; p.Audio.CrossfadeSeconds = 3;
                    var tl = Timeline.Compute(p.Tracks, p.Audio);
                    double expected = mode switch { TransitionMode.Silence => 35, TransitionMode.Crossfade => 25, _ => 31 };
                    Expect(Math.Abs(tl.Total - expected) < 0.05, $"расчёт {tl.Total}, ожидалось {expected}");
                    var album = AudioAlbumBuilder.BuildAsync(p, null, CancellationToken.None).GetAwaiter().GetResult();
                    Expect(Math.Abs(album.ActualDuration - tl.Total) < 0.05, $"WAV {album.ActualDuration} ≠ {tl.Total}");
                    var lines = tl.ChaptersText().Split('\n');
                    Expect(lines[0].StartsWith("00:00"), "первая строка " + lines[0]);
                    return tl.ChaptersText().Replace("\n", " | ").Replace("\r", "");
                });
            }

            Run("Тайм-коды для видео длиннее часа", () =>
            {
                var tracks = Enumerable.Range(0, 15).Select(i => new TrackItem { Title = "S" + i, Duration = 299.5 }).ToList();
                var tl = Timeline.Compute(tracks, new AudioSettings { Transition = TransitionMode.Silence, GapSeconds = 2 });
                var last = tl.ChaptersText().Split('\n').Last();
                Expect(last.StartsWith("1:10:"), last);
                return last;
            });

            // 5, 7, 8, 9, 11. Тестовые видео со всеми визуализаторами и подсветками
            var styles = Enum.GetValues(typeof(VisualizerStyle)).Cast<VisualizerStyle>().ToArray();
            var highlights = Enum.GetValues(typeof(HighlightStyle)).Cast<HighlightStyle>().ToArray();
            var motions = new[] { ImageMotion.ZoomIn, ImageMotion.PanLeft, ImageMotion.Float, ImageMotion.ZoomOut, ImageMotion.None };
            for (int i = 0; i < styles.Length; i++)
            {
                var st = styles[i]; var hl = highlights[i]; var mo = motions[i];
                Run($"Тест 20 сек: {st} + подсветка {hl} + фото {mo}", () =>
                {
                    var p = NewProject();
                    p.Visualizer.Style = st; p.Overlay.Highlight = hl; p.Background.Motion = mo;
                    p.Background.Flicker = i % 2 == 0; p.Background.Particles = true; p.Visualizer.Speed = i == 1 ? 0.5 : 1;
                    p.Export.FileName = "test_" + st;
                    var o = VideoExporter.ExportAsync(p, true, 8, null, CancellationToken.None).GetAwaiter().GetResult();
                    Expect(File.Exists(o.OutputPath), "нет файла");
                    var frame = Path.Combine(Dir, $"frame_{st}.png");
                    Ff("-ss", "10", "-i", o.OutputPath, "-frames:v", "1", frame);
                    return $"{Path.GetFileName(o.OutputPath)} {Fmt.N3(o.Duration)} с";
                });
            }

            // 6. Видеофон во всех режимах цикла
            foreach (var lm in new[] { BackgroundLoopMode.Normal, BackgroundLoopMode.PingPong, BackgroundLoopMode.Crossfade, BackgroundLoopMode.Smart })
            {
                Run($"Видеофон: цикл {lm}", () =>
                {
                    var p = NewProject();
                    p.Background.FilePath = bgVid; p.Background.LoopMode = lm;
                    p.Export.FileName = "loop_" + lm;
                    var o = VideoExporter.ExportAsync(p, true, 3, null, CancellationToken.None).GetAwaiter().GetResult();
                    return Path.GetFileName(o.OutputPath) + (o.Notes.Count > 0 ? " · " + string.Join("; ", o.Notes) : "");
                });
            }

            // 12. Полный экспорт 1080p, проверка длительности
            Run("Полное видео 1080p 30 FPS", () =>
            {
                var p = NewProject();
                p.Export.Height = 1080; p.Audio.Transition = TransitionMode.Crossfade; p.Export.FileName = "full_1080p";
                var sw = Stopwatch.StartNew();
                var o = VideoExporter.ExportAsync(p, false, 0, null, CancellationToken.None).GetAwaiter().GetResult();
                var vi = MediaProbe.ProbeVideo(o.OutputPath);
                Expect(vi.Width == 1920 && vi.Height == 1080, $"{vi.Width}x{vi.Height}");
                Expect(Math.Abs(vi.Duration - 25) < 0.5, "длительность " + vi.Duration);
                return $"{Fmt.N3(vi.Duration)} с за {sw.Elapsed.TotalSeconds:0.0} с, {vi.Width}×{vi.Height}";
            });

            // 13. Отмена
            Run("Отмена экспорта удаляет незавершённый файл", () =>
            {
                var p = NewProject();
                p.Export.Height = 2160; p.Export.FileName = "cancel_test";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                bool cancelled = false;
                try { VideoExporter.ExportAsync(p, false, 0, null, cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                var left = Directory.GetFiles(p.Export.OutputFolder, "cancel_test*");
                Expect(cancelled, "исключение отмены не получено");
                Expect(left.Length == 0, "остались файлы: " + string.Join(", ", left));
                return "отменено, файлов не осталось";
            });

            // 10. Сохранение и открытие проекта
            Run("Сохранение и открытие проекта .elara", () =>
            {
                var p = NewProject();
                p.Visualizer.Style = VisualizerStyle.GlowingDots; p.Overlay.AlbumTitle = "Атлас несказанного"; p.Tracks[1].Title = "Изменённое название";
                var file = Path.Combine(Dir, "Мой альбом.elara");
                ProjectStore.Save(p, file);
                var q = ProjectStore.Load(file);
                Expect(q.Tracks.Count == 3 && q.Tracks[1].Title == "Изменённое название", "треки");
                Expect(q.Visualizer.Style == VisualizerStyle.GlowingDots && q.Overlay.AlbumTitle == "Атлас несказанного", "настройки");
                Expect(Math.Abs(q.Tracks[0].Duration - p.Tracks[0].Duration) < 1e-6, "длительность");
                return "ok";
            });

            // 14. Отсутствующие файлы
            Run("Предупреждение об отсутствующих файлах", () =>
            {
                var p = NewProject();
                p.Tracks.Add(new TrackItem { FilePath = Path.Combine(Dir, "нет_такого.mp3"), Title = "x" });
                var missing = ProjectStore.FindMissing(p);
                Expect(missing.Count == 1, "найдено " + missing.Count);
                bool threw = false;
                try { AudioAlbumBuilder.BuildAsync(p, null, CancellationToken.None).GetAwaiter().GetResult(); } catch (FileNotFoundException) { threw = true; }
                Expect(threw, "сборка не остановилась");
                return missing[0];
            });

            Run("Кадр предпросмотра", () =>
            {
                var p = NewProject();
                var tl = Timeline.Compute(p.Tracks, p.Audio);
                var png = VideoExporter.RenderPreviewFrameAsync(p, tl, 15, CancellationToken.None).GetAwaiter().GetResult();
                Expect(File.Exists(png) && new FileInfo(png).Length > 1000, "нет кадра");
                File.Copy(png, Path.Combine(Dir, "preview.png"), true);
                return "ok";
            });

            Run("Экспорт альбома в MP3", () =>
            {
                var p = NewProject();
                var album = AudioAlbumBuilder.BuildAsync(p, null, CancellationToken.None).GetAwaiter().GetResult();
                var mp3 = Path.Combine(Dir, "album.mp3");
                AudioAlbumBuilder.ExportAlbumAsync(album.WavPath, mp3, album.Timeline.Total, null, CancellationToken.None).GetAwaiter().GetResult();
                Expect(Math.Abs(MediaProbe.ProbeDuration(mp3) - album.Timeline.Total) < 0.2, "длительность mp3");
                return "ok";
            });

            Run("YouTube-описание с тайм-кодами", () =>
            {
                var p = NewProject();
                var tl = Timeline.Compute(p.Tracks, p.Audio);
                YouTubeTextBuilder.Generate(p, tl);
                Expect(p.YouTube.Description.Contains(tl.ChaptersText()), "нет тайм-кодов");
                return p.YouTube.Title;
            });

            // Отчёт
            var sb = new StringBuilder();
            sb.AppendLine("# Результаты автотестов движка Elara Music Builder");
            sb.AppendLine();
            sb.AppendLine("FFmpeg: `" + FfmpegService.VersionText() + "`");
            sb.AppendLine();
            sb.AppendLine("| Тест | Результат | Подробности |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in Results) sb.AppendLine($"| {r.name} | {(r.ok ? "✅ пройден" : "❌ ошибка")} | {r.info.Replace("|", "/").Replace("\n", " ")} |");
            int failed = Results.Count(r => !r.ok);
            sb.AppendLine();
            sb.AppendLine($"Итого: {Results.Count - failed} из {Results.Count} пройдено.");
            File.WriteAllText("test-report.md", sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine(sb.ToString());
            Console.WriteLine("Файлы тестов: " + Dir);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Func<string> test)
        {
            Console.WriteLine("▶ " + name);
            try
            {
                var info = test();
                Results.Add((name, true, info ?? ""));
                Console.WriteLine("  ✅ " + info);
            }
            catch (Exception ex)
            {
                var msg = (ex.InnerException ?? ex).Message;
                Results.Add((name, false, msg));
                Console.WriteLine("  ❌ " + ex);
            }
        }

        private static void Expect(bool cond, string msg)
        {
            if (!cond) throw new Exception("Проверка не пройдена: " + msg);
        }

        private static string Tone(string name, int hz, int seconds)
        {
            var path = Path.Combine(Dir, name);
            Ff("-f", "lavfi", "-i", $"aevalsrc=0.3*sin(2*PI*{hz}*t)+0.4*sin(2*PI*60*t)*lt(mod(t\\,0.5)\\,0.08):s=48000:d={seconds}", "-ac", "2", path);
            return path;
        }

        private static void Ff(params string[] args)
        {
            var all = new List<string> { "-hide_banner", "-v", "error", "-y" };
            all.AddRange(args);
            var r = FfmpegService.RunAsync(all, Dir, null, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.Ok) throw new Exception("ffmpeg: " + r.ErrorTail);
        }
    }
}
