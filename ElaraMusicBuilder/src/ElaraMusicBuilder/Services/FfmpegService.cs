using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElaraMusicBuilder.Services
{
    public class FfmpegResult
    {
        public int ExitCode;
        public string StdErr;
        public bool Cancelled;
        public bool Ok => ExitCode == 0 && !Cancelled;

        /// <summary>Последние строки ошибок FFmpeg для понятного сообщения.</summary>
        public string ErrorTail
        {
            get
            {
                if (string.IsNullOrEmpty(StdErr)) return "";
                var lines = StdErr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 6)));
            }
        }
    }

    /// <summary>Поиск и запуск FFmpeg / FFprobe, которые поставляются вместе с программой.</summary>
    public static class FfmpegService
    {
        private static string _ffmpeg, _ffprobe;
        private static int _major = -1;
        private static string _encoderCache;

        public static string FfmpegPath => _ffmpeg ??= Locate("ffmpeg.exe");
        public static string FfprobePath => _ffprobe ??= Locate("ffprobe.exe");

        public static bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

        private static string Locate(string exe)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe);
            if (File.Exists(bundled)) return bundled;
            var local = Path.Combine(AppContext.BaseDirectory, exe);
            if (File.Exists(local)) return local;
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return bundled;
        }

        public static string VersionText()
        {
            try
            {
                var r = RunCapture(FfmpegPath, new[] { "-hide_banner", "-version" }, null, 15000);
                return r.Split('\n').FirstOrDefault()?.Trim() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Мажорная версия FFmpeg (для выбора синтаксиса загрузки графа фильтров из файла).</summary>
        public static int MajorVersion
        {
            get
            {
                if (_major >= 0) return _major;
                var v = VersionText();
                var m = Regex.Match(v, @"version\s+n?(\d+)\.");
                _major = m.Success ? int.Parse(m.Groups[1].Value) : 99; // сборки master — новые
                return _major;
            }
        }

        /// <summary>Аргументы для загрузки графа фильтров из файла.</summary>
        public static IEnumerable<string> FilterScriptArgs(string scriptPath)
        {
            if (MajorVersion >= 7) return new[] { "-/filter_complex", scriptPath };
            return new[] { "-filter_complex_script", scriptPath };
        }

        /// <summary>Проверяет аппаратные кодеки H.264 настоящим тестовым кодированием.</summary>
        public static string DetectGpuEncoder()
        {
            if (_encoderCache != null) return _encoderCache == "" ? null : _encoderCache;
            _encoderCache = "";
            foreach (var enc in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            {
                try
                {
                    var args = new List<string> { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30", "-frames:v", "10", "-c:v", enc, "-f", "null", "-" };
                    var r = RunAsync(args, null, null, CancellationToken.None).GetAwaiter().GetResult();
                    if (r.Ok) { _encoderCache = enc; Log.Info("GPU-кодек найден: " + enc); break; }
                }
                catch (Exception ex) { Log.Warn("Проверка кодека " + enc + ": " + ex.Message); }
            }
            return _encoderCache == "" ? null : _encoderCache;
        }

        public static string EncoderDisplayName(string enc) => enc switch
        {
            "h264_nvenc" => "NVIDIA (NVENC)",
            "h264_qsv" => "Intel (Quick Sync)",
            "h264_amf" => "AMD (AMF)",
            _ => "Процессор (x264)"
        };

        /// <summary>
        /// Запускает ffmpeg. Прогресс берётся из -progress pipe:1 (секунды готового результата).
        /// Отмена завершает процесс; вызывающий код удаляет незавершённый файл.
        /// </summary>
        public static Task<FfmpegResult> RunAsync(IEnumerable<string> args, string workDir, Action<double> onProgressSeconds, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(FfmpegPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
                var list = args.ToList();
                if (onProgressSeconds != null)
                {
                    list.InsertRange(0, new[] { "-progress", "pipe:1", "-nostats" });
                }
                foreach (var a in list) psi.ArgumentList.Add(a);
                Log.Info("ffmpeg " + string.Join(" ", list.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));

                var err = new StringBuilder();
                using var p = new Process { StartInfo = psi };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (err)
                    {
                        if (err.Length > 400_000) err.Remove(0, 200_000);
                        err.AppendLine(e.Data);
                    }
                };
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null || onProgressSeconds == null) return;
                    if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
                    {
                        var v = e.Data.Substring(e.Data.IndexOf('=') + 1);
                        if (long.TryParse(v, out var us) && us > 0) onProgressSeconds(us / 1_000_000.0);
                    }
                };

                if (!p.Start()) throw new InvalidOperationException("Не удалось запустить FFmpeg.");
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();

                bool cancelled = false;
                while (!p.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        try { p.StandardInput.Write('q'); p.StandardInput.Flush(); } catch { }
                        if (!p.WaitForExit(3000))
                        {
                            try { p.Kill(true); } catch { }
                        }
                        break;
                    }
                }
                p.WaitForExit();
                string errText;
                lock (err) errText = err.ToString();
                var res = new FfmpegResult { ExitCode = cancelled ? -1 : p.ExitCode, StdErr = errText, Cancelled = cancelled };
                if (!res.Ok && !cancelled) Log.Warn("ffmpeg завершился с кодом " + res.ExitCode + Environment.NewLine + res.ErrorTail);
                return res;
            });
        }

        /// <summary>Запуск с чтением текстового вывода (ffprobe / -version).</summary>
        public static string RunCapture(string exe, IEnumerable<string> args, string workDir, int timeoutMs = 120000)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("Процесс не ответил вовремя: " + Path.GetFileName(exe)); }
            p.WaitForExit();
            return outTask.Result + errTask.Result;
        }

        /// <summary>Запуск ffmpeg с чтением двоичного stdout (сырые кадры для анализа цикла).</summary>
        public static byte[] RunBinary(IEnumerable<string> args, int timeoutMs = 300000)
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p.ErrorDataReceived += (s, e) => { };
            p.BeginErrorReadLine();
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("Анализ видео занял слишком много времени."); }
            copy.Wait();
            return ms.ToArray();
        }
    }
}
