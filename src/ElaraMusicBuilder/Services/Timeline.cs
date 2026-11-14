using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    public class TimelineEntry
    {
        public int Index;
        public string Title;
        public double Start;
        public double End;
        public double Duration;
        /// <summary>Длительность перекрытия с предыдущим треком (для кроссфейда).</summary>
        public double OverlapWithPrevious;
    }

    public class Timeline
    {
        public List<TimelineEntry> Entries = new List<TimelineEntry>();
        public double Total;

        public bool IsComplete { get; private set; }

        /// <summary>
        /// Точный расчёт начала каждой песни в итоговом альбоме.
        /// Пауза сдвигает следующие песни вперёд, кроссфейд — назад (песни перекрываются).
        /// </summary>
        public static Timeline Compute(IList<TrackItem> tracks, AudioSettings a)
        {
            var tl = new Timeline { IsComplete = tracks.Count > 0 && tracks.All(t => t.Duration > 0) };
            double cursor = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                double d = Math.Max(0, tracks[i].Duration);
                double overlap = 0;
                if (i > 0)
                {
                    var prev = tl.Entries[i - 1];
                    switch (a.Transition)
                    {
                        case TransitionMode.Silence:
                            cursor += a.GapSeconds;
                            break;
                        case TransitionMode.Crossfade:
                            overlap = CrossfadeFor(prev.Duration, d, a.CrossfadeSeconds);
                            cursor -= overlap;
                            break;
                    }
                }
                var e = new TimelineEntry
                {
                    Index = i,
                    Title = string.IsNullOrWhiteSpace(tracks[i].Title) ? $"Track {i + 1}" : tracks[i].Title.Trim(),
                    Start = cursor,
                    Duration = d,
                    End = cursor + d,
                    OverlapWithPrevious = overlap
                };
                tl.Entries.Add(e);
                cursor += d;
            }
            tl.Total = Math.Max(0, cursor);
            return tl;
        }

        /// <summary>Кроссфейд не может быть длиннее половины любого из двух соседних треков.</summary>
        public static double CrossfadeFor(double prevDur, double curDur, double wanted)
        {
            double max = Math.Min(prevDur, curDur) / 2.0;
            return Math.Max(0, Math.Min(wanted, max));
        }

        /// <summary>Индекс песни, которая звучит в момент t (при кроссфейде — новая песня).</summary>
        public int IndexAt(double t)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (t >= Entries[i].Start) return i;
            return 0;
        }

        /// <summary>Интервал подсветки песни: до начала следующей (чтобы подсветка не накладывалась).</summary>
        public (double start, double end) HighlightRange(int i)
        {
            double s = Entries[i].Start;
            double e = i + 1 < Entries.Count ? Entries[i + 1].Start : Total;
            return (s, Math.Max(s, e));
        }

        public string ChaptersText()
        {
            bool longFmt = Total >= 3600;
            var sb = new StringBuilder();
            foreach (var e in Entries)
                sb.AppendLine($"{Fmt.Chapter(e.Start, longFmt)} {e.Title}");
            return sb.ToString().TrimEnd();
        }
    }
}
