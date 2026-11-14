using System;
using System.Linq;
using System.Text;
using ElaraMusicBuilder.Models;

namespace ElaraMusicBuilder.Services
{
    /// <summary>
    /// Шаблоны для публикации на YouTube. Все поля можно редактировать.
    /// Программа не обещает просмотров или монетизации — это только удобная заготовка.
    /// </summary>
    public static class YouTubeTextBuilder
    {
        public static void Generate(ProjectModel p, Timeline tl)
        {
            var y = p.YouTube;
            var o = p.Overlay;
            string artist = string.IsNullOrWhiteSpace(o.ArtistName) ? "Artist" : o.ArtistName.Trim();
            string album = string.IsNullOrWhiteSpace(o.AlbumTitle) ? "Album" : o.AlbumTitle.Trim();
            int minutes = (int)Math.Round(tl.Total / 60.0);
            bool ru = y.Language == "RU";

            y.Title = ru
                ? $"{artist} — {album} | Полный альбом ({tl.Entries.Count} песен, {minutes} мин)"
                : $"{artist} — {album} | Full Album ({tl.Entries.Count} Songs, {minutes} min)";
            if (y.Title.Length > 100) y.Title = y.Title.Substring(0, 97) + "…";

            var tags = new[] { Tag(artist), Tag(album), "#GothicMetal", "#SymphonicMetal", "#FullAlbum" };
            y.Hashtags = string.Join(" ", tags.Where(t => t.Length > 1).Distinct());
            y.Keywords = string.Join(", ", new[] { artist, album, "gothic metal", "symphonic metal", "full album", "female vocals", "dark fantasy music" });

            var sb = new StringBuilder();
            if (ru)
            {
                sb.AppendLine($"{artist} — «{album}». Полный альбом: {tl.Entries.Count} песен, около {minutes} минут музыки.");
                sb.AppendLine();
                sb.AppendLine("Треклист:");
            }
            else
            {
                sb.AppendLine($"{artist} — \"{album}\". The complete album: {tl.Entries.Count} songs, about {minutes} minutes of music.");
                sb.AppendLine();
                sb.AppendLine("Tracklist:");
            }
            sb.AppendLine(tl.ChaptersText());
            sb.AppendLine();
            sb.AppendLine(ru
                ? "Музыка и изображения созданы с использованием инструментов искусственного интеллекта."
                : "Music and visuals were created with the help of AI tools.");
            sb.AppendLine();
            sb.AppendLine(y.Hashtags);
            y.Description = sb.ToString().TrimEnd();
        }

        private static string Tag(string s)
        {
            var clean = new string(s.Where(char.IsLetterOrDigit).ToArray());
            return clean.Length == 0 ? "" : "#" + clean;
        }

        public static string FullText(ProjectModel p)
        {
            var y = p.YouTube;
            var sb = new StringBuilder();
            sb.AppendLine(y.Language == "RU" ? "НАЗВАНИЕ:" : "TITLE:");
            sb.AppendLine(y.Title);
            sb.AppendLine();
            sb.AppendLine(y.Language == "RU" ? "ОПИСАНИЕ:" : "DESCRIPTION:");
            sb.AppendLine(y.Description);
            sb.AppendLine();
            sb.AppendLine(y.Language == "RU" ? "КЛЮЧЕВЫЕ СЛОВА:" : "TAGS:");
            sb.AppendLine(y.Keywords);
            return sb.ToString();
        }
    }
}
