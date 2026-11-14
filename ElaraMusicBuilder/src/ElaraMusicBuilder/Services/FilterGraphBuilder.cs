using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    public class RenderContext
    {
        public ProjectModel Project;
        public Timeline Timeline;
        public int W, H, Fps;
        /// <summary>Время альбома, соответствующее первому кадру (для теста и предпросмотра).</summary>
        public double Offset;
        public string WorkDir;
        public bool BackgroundIsImage;
        public bool HasLogo;
    }

    /// <summary>
    /// Собирает граф фильтров FFmpeg: фон → анимация → частицы → визуализатор → тексты → логотип.
    /// Входы: 0 — фон (картинка, видео или цветной фон), 1 — звук альбома, 2 — логотип (необязательно).
    /// Все визуализаторы строятся из настоящего звука (showwaves / showfreqs), без случайной анимации.
    /// </summary>
    public static class FilterGraphBuilder
    {
        private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2.0) * 2);

        public static string Build(RenderContext c)
        {
            var p = c.Project;
            var g = new StringBuilder();
            string T = c.Offset > 0.0005 ? $"(t+{Fmt.N(c.Offset)})" : "t";
            double sc = c.H / 1080.0;

            // ---------- 1. Фон ----------
            var bg = p.Background;
            string fit = $"scale={c.W}:{c.H}:force_original_aspect_ratio=increase,crop={c.W}:{c.H},setsar=1";
            if (c.BackgroundIsImage && bg.Motion != ImageMotion.None)
            {
                double pre = c.H >= 2000 ? 1.25 : 1.5; // запас разрешения для плавного движения
                int W2 = Even(c.W * pre), H2 = Even(c.H * pre);
                double P = Math.Max(1, bg.MotionPeriod * c.Fps);
                string on = c.Offset > 0.0005 ? $"(on+{Fmt.N(Math.Round(c.Offset * c.Fps))})" : "on";
                string ph = $"2*PI*{on}/{Fmt.N(P)}";
                string z, x, y;
                string cx = "iw/2-(iw/zoom/2)", cy = "ih/2-(ih/zoom/2)";
                switch (bg.Motion)
                {
                    case ImageMotion.ZoomIn: z = $"1+0.10*(1-cos({ph}))/2"; x = cx; y = cy; break;
                    case ImageMotion.ZoomOut: z = $"1.10-0.10*(1-cos({ph}))/2"; x = cx; y = cy; break;
                    case ImageMotion.PanLeft: z = "1.12"; x = $"(iw-iw/zoom)*(0.5+0.5*cos({ph}))"; y = cy; break;
                    case ImageMotion.PanRight: z = "1.12"; x = $"(iw-iw/zoom)*(0.5-0.5*cos({ph}))"; y = cy; break;
                    default: z = "1.07"; x = $"(iw-iw/zoom)/2*(1+0.6*sin({ph}))"; y = $"(ih-ih/zoom)/2*(1+0.6*sin({ph}/1.37))"; break;
                }
                g.Append($"[0:v]scale={W2}:{H2}:force_original_aspect_ratio=increase,crop={W2}:{H2},setsar=1," +
                         $"zoompan=z='{z}':x='{x}':y='{y}':d=1:s={c.W}x{c.H}:fps={c.Fps},format=yuv420p");
            }
            else
            {
                g.Append($"[0:v]{fit},fps={c.Fps},format=yuv420p");
            }

            bool flicker = bg.Flicker;
            if (bg.Dim > 0.005 || flicker)
            {
                string b = "-" + Fmt.N(bg.Dim * 0.35);
                if (flicker) b += $"+0.025*sin(2*PI*{T}/2.7)+0.012*sin(2*PI*{T}/0.83)";
                g.Append($",eq=brightness='{b}'" + (flicker ? ":eval=frame" : ""));
            }
            g.Append("[bg0];");

            string cur = "bg0";

            // ---------- 2. Атмосферные частицы ----------
            if (bg.Particles)
            {
                // Маска частиц строится в низком разрешении и смешивается только по яркости (быстро и без засветки фона)
                string pcol = ColorUtil.Ff(bg.ParticleColor, "0xF5E6D0");
                int hw = Even(c.W / 2.0), hh = Even(c.H / 2.0);
                g.Append($"color=c=gray:s=192x108:r={c.Fps},noise=alls=100:allf=u,format=gray,lutyuv=y='if(gt(val\\,184)\\,255\\,0)'," +
                         $"scroll=v=-0.0015:h=0.0005,scale={hw}x{hh}:flags=bicubic,gblur=sigma={Fmt.N3(1.25 * c.H / 720.0)}," +
                         $"scale={c.W}x{c.H}:flags=bilinear,format=yuv420p,lutyuv=y='min(255\\,max(0\\,val-16)*0.6)':u=128:v=128[pm];");
                g.Append($"color=c={pcol}:s={c.W}x{c.H}:r={c.Fps},format=yuv420p[pw];");
                g.Append($"[{cur}][pw][pm]maskedmerge=planes=1[bg1];");
                cur = "bg1";
            }

            // ---------- 3. Аудиовизуализатор ----------
            var v = p.Visualizer;
            if (v.Enabled)
            {
                string viz = BuildVisualizer(v, c, sc, out int vw);
                g.Append(viz);
                int xv = (int)Math.Round(c.W * v.X / 100.0);
                int yv = (int)Math.Round(c.H * v.Y / 100.0);
                g.Append($"[{cur}][viz]overlay=x={xv}:y={yv}:format=auto[v0];");
                cur = "v0";
            }

            // ---------- 4. Тексты ----------
            var texts = BuildTexts(c, T, sc);
            if (texts.Count > 0)
            {
                g.Append($"[{cur}]").Append(string.Join(",", texts)).Append("[vt];");
                cur = "vt";
            }

            // ---------- 5. Логотип ----------
            var o = p.Overlay;
            if (c.HasLogo)
            {
                int lw = Even(c.W * o.LogoWidth / 100.0);
                int lx = (int)Math.Round(c.W * o.LogoX / 100.0);
                int ly = (int)Math.Round(c.H * o.LogoY / 100.0);
                g.Append($"[2:v]scale={lw}:-1,format=rgba");
                if (o.LogoOpacity < 0.999) g.Append($",colorchannelmixer=aa={Fmt.N3(o.LogoOpacity)}");
                g.Append("[logo];");
                g.Append($"[{cur}][logo]overlay=x={lx}:y={ly}:format=auto[vl];");
                cur = "vl";
            }

            g.Append($"[{cur}]format=yuv420p[vout]");
            return g.ToString();
        }

        private static string BuildVisualizer(VisualizerSettings v, RenderContext c, double sc, out int vw)
        {
            var s = new StringBuilder();
            int Wv = Even(c.W * v.Width / 100.0);
            int Hv = Even(c.H * v.Height / 100.0);
            double k = Math.Max(1, v.Thickness);
            int avg = 1 + (int)Math.Round(v.Smoothness * 6);
            bool isWave = v.Style == VisualizerStyle.LaserWave || v.Style == VisualizerStyle.SymmetricPulse;

            s.Append($"[1:a]aformat=sample_fmts=fltp:channel_layouts=stereo,volume={Fmt.N3(v.Sensitivity)}");
            if (v.BassBoost > 0.1) s.Append($",bass=g={Fmt.N3(v.BassBoost)}:f=100");
            if (isWave && v.Smoothness > 0.05) s.Append($",lowpass=f={Fmt.I((int)(9000 - v.Smoothness * 7500))}");
            s.Append(',');

            int iw = Even(Wv / k), ih = Even(Hv / k);
            vw = Wv;
            switch (v.Style)
            {
                case VisualizerStyle.LaserWave:
                    s.Append($"showwaves=s={iw}x{ih}:mode=p2p:rate={c.Fps}:colors=white:scale=sqrt:draw=full,format=rgba,scale={Wv}x{Hv}:flags=bilinear");
                    break;
                case VisualizerStyle.SymmetricPulse:
                    s.Append($"showwaves=s={iw}x{ih}:mode=cline:rate={c.Fps}:colors=white:scale=sqrt:draw=full,format=rgba,scale={Wv}x{Hv}:flags=bilinear");
                    break;
                case VisualizerStyle.SpectrumBars:
                {
                    int nb = Math.Clamp((int)Math.Round(Wv / (24.0 * sc * (k / 3.0 + 0.5))), 16, 128);
                    int bw = Math.Max(4, Wv / nb);
                    vw = bw * nb;
                    int gap = Math.Max(2, bw / 4);
                    s.Append($"showfreqs=s={nb}x{Math.Max(16, Hv / 4)}:mode=bar:ascale=log:fscale=log:win_size=2048:averaging={avg}:cmode=combined:colors=white|white," +
                             $"format=rgba,scale={vw}x{Hv}:flags=neighbor,drawgrid=w={bw}:h={Hv + 10}:t={gap}:c=black@0:replace=1");
                    break;
                }
                case VisualizerStyle.SpectralLine:
                    s.Append($"showfreqs=s={iw}x{ih}:mode=line:ascale=log:fscale=log:win_size=2048:averaging={avg}:cmode=combined:colors=white|white,format=rgba,scale={Wv}x{Hv}:flags=bilinear");
                    break;
                default: // GlowingDots
                {
                    int dw = Even(Wv / (k + 2)), dh = Even(Hv / (k + 2));
                    s.Append($"showfreqs=s={dw}x{dh}:mode=dot:ascale=log:fscale=log:win_size=2048:averaging={avg}:cmode=combined:colors=white|white,format=rgba,scale={Wv}x{Hv}:flags=neighbor");
                    break;
                }
            }

            // лёгкое сглаживание «лесенки» у линий после увеличения
            if (isWave || v.Style == VisualizerStyle.SpectralLine) s.Append($",gblur=sigma={Fmt.N3(Math.Max(0.5, 0.9 * sc))},format=rgba");
            s.Append($",colorchannelmixer={ColorUtil.Mixer(v.Color)}");

            if (v.Speed < 0.98)
            {
                int frames = Math.Clamp((int)Math.Round(1.0 / v.Speed), 2, 4);
                s.Append($",tmix=frames={frames}");
            }

            if (v.Glow > 0.02)
            {
                double sigma = (3 + v.Glow * 12) * sc;
                double gain = 1 + v.Glow * 5;
                s.Append($",split[vz1][vz2];[vz2]alphaextract,gblur=sigma={Fmt.N3(sigma)},lutyuv=y='min(255\\,val*{Fmt.N3(gain)})'[vza];");
                s.Append($"color=c={ColorUtil.Ff(v.Color)}:s={vw}x{Hv}:r={c.Fps},format=rgba[vzc];[vzc][vza]alphamerge[vzg];[vzg][vz1]overlay=format=auto");
            }

            if (v.Opacity < 0.999) s.Append($",colorchannelmixer=aa={Fmt.N3(v.Opacity)}");
            s.Append("[viz];");
            return s.ToString();
        }

        private static string WriteText(RenderContext c, string name, string text)
        {
            var clean = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            File.WriteAllText(Path.Combine(c.WorkDir, name), clean, new UTF8Encoding(false));
            return name;
        }

        /// <summary>Копирует шрифт во временную папку, чтобы путь не содержал двоеточий и пробелов.</summary>
        private static string PrepareFont(RenderContext c, string fontPath, string alias, bool title)
        {
            var src = FontCatalog.Resolve(fontPath, title);
            var name = alias + Path.GetExtension(src).ToLowerInvariant();
            var dst = Path.Combine(c.WorkDir, name);
            if (!File.Exists(dst)) File.Copy(src, dst, true);
            return name;
        }

        private static List<string> BuildTexts(RenderContext c, string T, double sc)
        {
            var o = c.Project.Overlay;
            var list = new List<string>();
            var tl = c.Timeline;
            bool any = o.ShowAlbum || o.ShowArtist || (o.ShowTracklist && tl.Entries.Count > 0) || (o.ShowCurrentSong && tl.Entries.Count > 0) || (o.ShowCustomText && !string.IsNullOrWhiteSpace(o.CustomText));
            if (!any) return list;

            string ft = PrepareFont(c, o.TitleFont, "ft", true);
            string fb = PrepareFont(c, o.BodyFont, "fb", false);
            string shadow = o.TextShadow ? $":shadowx={Math.Max(1, (int)Math.Round(2 * sc))}:shadowy={Math.Max(1, (int)Math.Round(2 * sc))}:shadowcolor=black@0.55" : "";
            string titleCol = ColorUtil.Ff(o.TitleColor);
            string textCol = ColorUtil.Ff(o.TextColor);
            string hl = ColorUtil.Ff(o.HighlightColor);

            string DT(string font, string file, double x, double y, double size, string color, string extra = "")
                => $"drawtext=fontfile={font}:textfile={file}:expansion=none:x={Fmt.N3(x)}:y={Fmt.N3(y)}:fontsize={Fmt.I(Math.Max(6, (int)Math.Round(size)))}:fontcolor={color}{shadow}{extra}";

            // Название альбома и артист
            double tx = c.W * o.TitleX / 100.0, ty = c.H * o.TitleY / 100.0;
            if (o.ShowAlbum && !string.IsNullOrWhiteSpace(o.AlbumTitle))
            {
                list.Add(DT(ft, WriteText(c, "album.txt", o.AlbumTitle), tx, ty, o.TitleSize * sc, titleCol));
                ty += o.TitleSize * sc * 1.3;
            }
            if (o.ShowArtist && !string.IsNullOrWhiteSpace(o.ArtistName))
                list.Add(DT(fb, WriteText(c, "artist.txt", o.ArtistName), tx, ty, o.ArtistSize * sc, hl + "@0.95"));

            // Треклист с подсветкой текущей песни
            if (o.ShowTracklist && tl.Entries.Count > 0)
            {
                double ts = o.TrackSize * sc;
                double lineH = ts * 1.55;
                double x0 = c.W * o.TracklistX / 100.0, y0 = c.H * o.TracklistY / 100.0;
                double avail = c.H * 0.97 - y0;
                int rows = Math.Max(1, Math.Min(o.MaxRows, (int)Math.Floor(avail / lineH)));
                double colW = c.W * o.TracklistColumnWidth / 100.0;
                File.WriteAllText(Path.Combine(c.WorkDir, "marker.txt"), "●", new UTF8Encoding(false));

                for (int i = 0; i < tl.Entries.Count; i++)
                {
                    var e = tl.Entries[i];
                    int col = i / rows, row = i % rows;
                    double x = x0 + col * colW, y = y0 + row * lineH;
                    string label = o.ShowNumbers ? $"{i + 1:00}.  {e.Title}" : e.Title;
                    string file = WriteText(c, $"tl_{i}.txt", label);
                    var (s, en) = tl.HighlightRange(i);
                    string between = $"between({T},{Fmt.N3(s)},{Fmt.N3(en)})";

                    list.Add(DT(fb, file, x, y, ts, textCol + "@0.82", $":enable='not({between})'"));

                    switch (o.Highlight)
                    {
                        case HighlightStyle.Color:
                            list.Add(DT(fb, file, x, y, ts, hl, $":enable='{between}'"));
                            break;
                        case HighlightStyle.Glow:
                            list.Add(DT(fb, file, x, y, ts, hl, $":borderw={Math.Max(1, (int)Math.Round(3 * sc))}:bordercolor={hl}@0.35:enable='{between}'"));
                            break;
                        case HighlightStyle.Underline:
                        {
                            list.Add(DT(fb, file, x, y, ts, hl, $":enable='{between}'"));
                            int uw = (int)Math.Round(Math.Min(colW * 0.95, label.Length * ts * 0.52));
                            list.Add($"drawbox=x={(int)x}:y={(int)(y + ts * 1.22)}:w={Math.Max(10, uw)}:h={Math.Max(2, (int)Math.Round(2 * sc))}:color={hl}@0.9:t=fill:enable='{between}'");
                            break;
                        }
                        case HighlightStyle.Fade:
                            list.Add(DT(fb, file, x, y, ts, hl, $":alpha='min(1,max(0,({T}-{Fmt.N3(s)})/0.8))':enable='{between}'"));
                            break;
                        default: // Marker
                        {
                            list.Add(DT(fb, file, x, y, ts, hl, $":enable='{between}'"));
                            double mx = x - ts * 1.05;
                            list.Add($"drawtext=fontfile={fb}:textfile=marker.txt:expansion=none:x='{Fmt.N3(mx)}+{Fmt.N3(4 * sc)}*sin(2*PI*{T})':y={Fmt.N3(y + ts * 0.18)}:fontsize={Fmt.I(Math.Max(6, (int)Math.Round(ts * 0.7)))}:fontcolor={hl}:enable='{between}'");
                            break;
                        }
                    }
                }
            }

            // Название текущей песни (плавное появление и исчезновение)
            if (o.ShowCurrentSong && tl.Entries.Count > 0)
            {
                double cx = c.W * o.CurrentX / 100.0, cy = c.H * o.CurrentY / 100.0;
                for (int i = 0; i < tl.Entries.Count; i++)
                {
                    var (s, en) = tl.HighlightRange(i);
                    if (en - s < 0.1) continue;
                    string S = Fmt.N3(s), E = Fmt.N3(en);
                    string alpha = $"if(lt({T}-{S},1),max(0,{T}-{S}),if(gt({T},{E}-1),max(0,{E}-{T}),1))";
                    list.Add(DT(ft, WriteText(c, $"cur_{i}.txt", tl.Entries[i].Title), cx, cy, o.CurrentSize * sc, titleCol,
                        $":alpha='{alpha}':enable='between({T},{S},{E})'"));
                }
            }

            if (o.ShowCustomText && !string.IsNullOrWhiteSpace(o.CustomText))
                list.Add(DT(fb, WriteText(c, "custom.txt", o.CustomText), c.W * o.CustomX / 100.0, c.H * o.CustomY / 100.0, o.CustomSize * sc, textCol));

            return list;
        }
    }
}
