using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    /// <summary>
    /// Демо-проект из собственных синтезированных материалов (без чужих прав):
    /// три короткие мелодии и градиентный фон, сгенерированные FFmpeg на компьютере пользователя.
    /// </summary>
    public static class DemoProject
    {
        public static async Task<ProjectModel> CreateAsync(CancellationToken ct)
        {
            VideoExporter.EnsureFfmpeg();
            var dir = AppPaths.Demo;
            var tracks = new (string file, string title, string expr, int dur)[]
            {
                ("demo_1.wav", "Демо — Сияние", "0.25*sin(2*PI*220*t)*(0.6+0.4*sin(2*PI*0.5*t))+0.2*sin(2*PI*330*t)+0.35*sin(2*PI*55*t)*lt(mod(t,0.5),0.08)", 24),
                ("demo_2.wav", "Демо — Далёкие огни", "0.25*sin(2*PI*196*t)+0.18*sin(2*PI*293.66*t)*(0.5+0.5*sin(2*PI*0.25*t))+0.35*sin(2*PI*60*t)*lt(mod(t,0.75),0.1)", 20),
                ("demo_3.wav", "Демо — В потоке", "0.22*sin(2*PI*261.63*t)+0.18*sin(2*PI*392*t)*(0.5+0.5*sin(2*PI*1*t))+0.35*sin(2*PI*50*t)*lt(mod(t,0.4),0.07)", 22),
            };
            var p = new ProjectModel { Name = "Демо-проект" };
            foreach (var t in tracks)
            {
                var path = Path.Combine(dir, t.file);
                if (!File.Exists(path))
                {
                    var expr = t.expr.Replace(",", "\\,");
                    var r = await FfmpegService.RunAsync(new List<string> { "-hide_banner", "-y", "-f", "lavfi", "-i", $"aevalsrc={expr}:s=48000:d={t.dur}", "-af", "afade=t=in:d=1,aecho=0.8:0.6:300:0.3", "-ac", "2", path }, dir, null, ct);
                    if (!r.Ok) throw new InvalidOperationException("Не удалось создать демо-музыку." + Environment.NewLine + r.ErrorTail);
                }
                var fi = new FileInfo(path);
                p.Tracks.Add(new TrackItem { FilePath = path, Title = t.title, Format = "WAV", FileSize = fi.Length, FileTimeUtc = fi.LastWriteTimeUtc });
            }
            var bg = Path.Combine(dir, "demo_background.png");
            if (!File.Exists(bg))
            {
                var r = await FfmpegService.RunAsync(new List<string> { "-hide_banner", "-y", "-f", "lavfi", "-i", "gradients=s=1920x1080:c0=0x3B1A3A:c1=0x1B2A44:c2=0x0F1020:n=3:duration=1:seed=7", "-frames:v", "1", bg }, dir, null, ct);
                if (!r.Ok) throw new InvalidOperationException("Не удалось создать демо-фон." + Environment.NewLine + r.ErrorTail);
            }
            p.Background.FilePath = bg;
            p.Overlay.ArtistName = "ELARA MUSIC";
            p.Overlay.AlbumTitle = "ДЕМО-АЛЬБОМ";
            p.Export.FileName = "Demo_Elara";
            p.Audio.Transition = TransitionMode.Crossfade;
            p.Audio.CrossfadeSeconds = 3;
            return p;
        }
    }
}
