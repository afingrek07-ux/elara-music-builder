using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;

namespace ElaraMusicBuilder.Models
{
    public enum TransitionMode { None, Silence, FadeInOut, Crossfade }
    public enum LoudnessPreset { YouTube, Soft, None }
    public enum BackgroundLoopMode { Normal, PingPong, Crossfade, Smart }
    public enum ImageMotion { None, ZoomIn, ZoomOut, PanLeft, PanRight, Float }
    public enum VisualizerStyle { LaserWave, SpectrumBars, SpectralLine, GlowingDots, SymmetricPulse }
    public enum HighlightStyle { Color, Glow, Underline, Fade, Marker }
    public enum ExportQuality { Fast, Balanced, High }
    public enum PlaceTarget { Visualizer, Title, Tracklist, CurrentSong, Logo, CustomText }

    public class TrackItem : ObservableObject
    {
        private string _filePath;
        private string _title;
        private double _duration;
        private string _format;
        private int _number;
        private bool _isMissing;
        private bool _isMeasuring;

        public string FilePath { get => _filePath; set => Set(ref _filePath, value); }
        public string Title { get => _title; set => Set(ref _title, value); }

        /// <summary>Точная длительность в секундах (по количеству сэмплов после декодирования).</summary>
        public double Duration
        {
            get => _duration;
            set { if (Set(ref _duration, value)) OnPropertyChanged(nameof(DurationText)); }
        }

        public string Format { get => _format; set => Set(ref _format, value); }

        [JsonIgnore] public int Number { get => _number; set => Set(ref _number, value); }
        [JsonIgnore] public bool IsMissing { get => _isMissing; set => Set(ref _isMissing, value); }

        [JsonIgnore]
        public bool IsMeasuring
        {
            get => _isMeasuring;
            set { if (Set(ref _isMeasuring, value)) OnPropertyChanged(nameof(DurationText)); }
        }

        [JsonIgnore]
        public string DurationText => IsMeasuring ? "…" : (Duration > 0 ? Services.Fmt.Time(Duration) : "—");

        /// <summary>Размер и дата изменения файла: используются для кэша длительности.</summary>
        public long FileSize { get; set; }
        public DateTime FileTimeUtc { get; set; }
    }

    public class AudioSettings : ObservableObject
    {
        private TransitionMode _transition = TransitionMode.Silence;
        private double _gapSeconds = 2;
        private double _crossfadeSeconds = 4;
        private double _trackFadeSeconds = 1.5;
        private bool _albumFadeIn = true;
        private double _albumFadeInSeconds = 2;
        private bool _albumFadeOut = true;
        private double _albumFadeOutSeconds = 5;
        private LoudnessPreset _loudness = LoudnessPreset.YouTube;

        public TransitionMode Transition { get => _transition; set => Set(ref _transition, value); }
        public double GapSeconds { get => _gapSeconds; set => Set(ref _gapSeconds, Math.Clamp(value, 0, 10)); }
        public double CrossfadeSeconds { get => _crossfadeSeconds; set => Set(ref _crossfadeSeconds, Math.Clamp(value, 0, 10)); }
        public double TrackFadeSeconds { get => _trackFadeSeconds; set => Set(ref _trackFadeSeconds, Math.Clamp(value, 0, 10)); }
        public bool AlbumFadeIn { get => _albumFadeIn; set => Set(ref _albumFadeIn, value); }
        public double AlbumFadeInSeconds { get => _albumFadeInSeconds; set => Set(ref _albumFadeInSeconds, Math.Clamp(value, 0, 15)); }
        public bool AlbumFadeOut { get => _albumFadeOut; set => Set(ref _albumFadeOut, value); }
        public double AlbumFadeOutSeconds { get => _albumFadeOutSeconds; set => Set(ref _albumFadeOutSeconds, Math.Clamp(value, 0, 20)); }
        public LoudnessPreset Loudness { get => _loudness; set => Set(ref _loudness, value); }
    }

    public class BackgroundSettings : ObservableObject
    {
        private string _filePath;
        private BackgroundLoopMode _loopMode = BackgroundLoopMode.Smart;
        private ImageMotion _motion = ImageMotion.ZoomIn;
        private double _motionPeriod = 60;
        private bool _flicker;
        private bool _particles = true;
        private string _particleColor = "#F5E6D0";
        private double _dim = 0.1;

        public string FilePath
        {
            get => _filePath;
            set { if (Set(ref _filePath, value)) { OnPropertyChanged(nameof(IsVideo)); OnPropertyChanged(nameof(IsImage)); OnPropertyChanged(nameof(FileName)); } }
        }

        public BackgroundLoopMode LoopMode { get => _loopMode; set => Set(ref _loopMode, value); }
        public ImageMotion Motion { get => _motion; set => Set(ref _motion, value); }
        /// <summary>Период одного цикла движения, секунд (больше — медленнее).</summary>
        public double MotionPeriod { get => _motionPeriod; set => Set(ref _motionPeriod, Math.Clamp(value, 15, 180)); }
        public bool Flicker { get => _flicker; set => Set(ref _flicker, value); }
        public bool Particles { get => _particles; set => Set(ref _particles, value); }
        public string ParticleColor { get => _particleColor; set => Set(ref _particleColor, value); }
        /// <summary>Затемнение фона 0..0.6.</summary>
        public double Dim { get => _dim; set => Set(ref _dim, Math.Clamp(value, 0, 0.6)); }

        [JsonIgnore] public bool IsVideo => Services.MediaTypes.IsVideo(FilePath);
        [JsonIgnore] public bool IsImage => Services.MediaTypes.IsImage(FilePath);
        [JsonIgnore] public string FileName => string.IsNullOrEmpty(FilePath) ? "Фон не выбран" : Path.GetFileName(FilePath);
    }

    public class VisualizerSettings : ObservableObject
    {
        private bool _enabled = true;
        private VisualizerStyle _style = VisualizerStyle.LaserWave;
        private string _color = "#F17C8A";
        private double _x = 5, _y = 74, _width = 90, _height = 18;
        private double _opacity = 0.95, _glow = 0.6, _thickness = 3, _sensitivity = 1.2, _bassBoost = 4, _smoothness = 0.4, _speed = 1.0;

        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public VisualizerStyle Style { get => _style; set => Set(ref _style, value); }
        public string Color { get => _color; set => Set(ref _color, value); }
        /// <summary>Положение и размер в процентах от кадра.</summary>
        public double X { get => _x; set => Set(ref _x, Math.Clamp(value, 0, 100)); }
        public double Y { get => _y; set => Set(ref _y, Math.Clamp(value, 0, 100)); }
        public double Width { get => _width; set => Set(ref _width, Math.Clamp(value, 5, 100)); }
        public double Height { get => _height; set => Set(ref _height, Math.Clamp(value, 3, 100)); }
        public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1)); }
        public double Glow { get => _glow; set => Set(ref _glow, Math.Clamp(value, 0, 1)); }
        public double Thickness { get => _thickness; set => Set(ref _thickness, Math.Clamp(value, 1, 6)); }
        public double Sensitivity { get => _sensitivity; set => Set(ref _sensitivity, Math.Clamp(value, 0.2, 4)); }
        public double BassBoost { get => _bassBoost; set => Set(ref _bassBoost, Math.Clamp(value, 0, 15)); }
        public double Smoothness { get => _smoothness; set => Set(ref _smoothness, Math.Clamp(value, 0, 1)); }
        public double Speed { get => _speed; set => Set(ref _speed, Math.Clamp(value, 0.25, 1.0)); }
    }

    public class OverlaySettings : ObservableObject
    {
        private string _artist = "ELARA MORVAYNE";
        private string _album = "THE ATLAS OF UNSPOKEN THINGS";
        private bool _showArtist = true, _showAlbum = true, _showTracklist = true, _showCurrentSong = true, _showNumbers = true, _textShadow = true;
        private string _titleFont, _bodyFont;
        private string _titleColor = "#E8E6EF", _textColor = "#D9D6E2", _highlightColor = "#F17C8A";
        private HighlightStyle _highlight = HighlightStyle.Glow;
        private double _titleSize = 54, _artistSize = 30, _trackSize = 26, _currentSize = 40, _customSize = 26;
        private double _titleX = 5, _titleY = 6, _tracklistX = 5, _tracklistY = 22, _currentX = 55, _currentY = 64, _customX = 70, _customY = 92;
        private double _tracklistColumnWidth = 32;
        private int _maxRows = 16;
        private string _logoPath;
        private bool _showLogo = true;
        private double _logoX = 90, _logoY = 5, _logoWidth = 7, _logoOpacity = 1;
        private string _customText = "";
        private bool _showCustomText;

        public string ArtistName { get => _artist; set => Set(ref _artist, value); }
        public string AlbumTitle { get => _album; set => Set(ref _album, value); }
        public bool ShowArtist { get => _showArtist; set => Set(ref _showArtist, value); }
        public bool ShowAlbum { get => _showAlbum; set => Set(ref _showAlbum, value); }
        public bool ShowTracklist { get => _showTracklist; set => Set(ref _showTracklist, value); }
        public bool ShowCurrentSong { get => _showCurrentSong; set => Set(ref _showCurrentSong, value); }
        public bool ShowNumbers { get => _showNumbers; set => Set(ref _showNumbers, value); }
        public bool TextShadow { get => _textShadow; set => Set(ref _textShadow, value); }
        public string TitleFont { get => _titleFont; set => Set(ref _titleFont, value); }
        public string BodyFont { get => _bodyFont; set => Set(ref _bodyFont, value); }
        public string TitleColor { get => _titleColor; set => Set(ref _titleColor, value); }
        public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
        public string HighlightColor { get => _highlightColor; set => Set(ref _highlightColor, value); }
        public HighlightStyle Highlight { get => _highlight; set => Set(ref _highlight, value); }
        /// <summary>Размеры шрифтов в пикселях для кадра 1080p (масштабируются под другое разрешение).</summary>
        public double TitleSize { get => _titleSize; set => Set(ref _titleSize, Math.Clamp(value, 10, 200)); }
        public double ArtistSize { get => _artistSize; set => Set(ref _artistSize, Math.Clamp(value, 8, 150)); }
        public double TrackSize { get => _trackSize; set => Set(ref _trackSize, Math.Clamp(value, 8, 120)); }
        public double CurrentSize { get => _currentSize; set => Set(ref _currentSize, Math.Clamp(value, 8, 160)); }
        public double CustomSize { get => _customSize; set => Set(ref _customSize, Math.Clamp(value, 8, 160)); }
        public double TitleX { get => _titleX; set => Set(ref _titleX, Math.Clamp(value, 0, 100)); }
        public double TitleY { get => _titleY; set => Set(ref _titleY, Math.Clamp(value, 0, 100)); }
        public double TracklistX { get => _tracklistX; set => Set(ref _tracklistX, Math.Clamp(value, 0, 100)); }
        public double TracklistY { get => _tracklistY; set => Set(ref _tracklistY, Math.Clamp(value, 0, 100)); }
        public double TracklistColumnWidth { get => _tracklistColumnWidth; set => Set(ref _tracklistColumnWidth, Math.Clamp(value, 10, 60)); }
        public int MaxRows { get => _maxRows; set => Set(ref _maxRows, Math.Clamp(value, 4, 40)); }
        public double CurrentX { get => _currentX; set => Set(ref _currentX, Math.Clamp(value, 0, 100)); }
        public double CurrentY { get => _currentY; set => Set(ref _currentY, Math.Clamp(value, 0, 100)); }
        public string LogoPath { get => _logoPath; set => Set(ref _logoPath, value); }
        public bool ShowLogo { get => _showLogo; set => Set(ref _showLogo, value); }
        public double LogoX { get => _logoX; set => Set(ref _logoX, Math.Clamp(value, 0, 100)); }
        public double LogoY { get => _logoY; set => Set(ref _logoY, Math.Clamp(value, 0, 100)); }
        public double LogoWidth { get => _logoWidth; set => Set(ref _logoWidth, Math.Clamp(value, 1, 50)); }
        public double LogoOpacity { get => _logoOpacity; set => Set(ref _logoOpacity, Math.Clamp(value, 0.05, 1)); }
        public string CustomText { get => _customText; set => Set(ref _customText, value); }
        public bool ShowCustomText { get => _showCustomText; set => Set(ref _showCustomText, value); }
        public double CustomX { get => _customX; set => Set(ref _customX, Math.Clamp(value, 0, 100)); }
        public double CustomY { get => _customY; set => Set(ref _customY, Math.Clamp(value, 0, 100)); }
    }

    public class ExportSettings : ObservableObject
    {
        private int _height = 1080;
        private int _fps = 30;
        private bool _useGpu = true;
        private ExportQuality _quality = ExportQuality.Balanced;
        private string _outputFolder;
        private string _fileName = "Elara_Album";

        /// <summary>Высота кадра: 720, 1080, 1440, 2160. Ширина — 16:9.</summary>
        public int Height { get => _height; set { if (Set(ref _height, value)) OnPropertyChanged(nameof(Width)); } }
        [JsonIgnore] public int Width => Height switch { 720 => 1280, 1440 => 2560, 2160 => 3840, _ => 1920 };
        public int Fps { get => _fps; set => Set(ref _fps, value); }
        public bool UseGpu { get => _useGpu; set => Set(ref _useGpu, value); }
        public ExportQuality Quality { get => _quality; set => Set(ref _quality, value); }
        public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, value); }
        public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    }

    public class YouTubeMeta : ObservableObject
    {
        private string _language = "EN";
        private string _title = "";
        private string _description = "";
        private string _hashtags = "";
        private string _keywords = "";

        public string Language { get => _language; set => Set(ref _language, value); }
        public string Title { get => _title; set => Set(ref _title, value); }
        public string Description { get => _description; set => Set(ref _description, value); }
        public string Hashtags { get => _hashtags; set => Set(ref _hashtags, value); }
        public string Keywords { get => _keywords; set => Set(ref _keywords, value); }
    }

    public class ProjectModel : ObservableObject
    {
        public int FormatVersion { get; set; } = 1;
        public string Name { get; set; } = "Новый проект";
        public ObservableCollection<TrackItem> Tracks { get; set; } = new ObservableCollection<TrackItem>();
        public AudioSettings Audio { get; set; } = new AudioSettings();
        public BackgroundSettings Background { get; set; } = new BackgroundSettings();
        public VisualizerSettings Visualizer { get; set; } = new VisualizerSettings();
        public OverlaySettings Overlay { get; set; } = new OverlaySettings();
        public ExportSettings Export { get; set; } = new ExportSettings();
        public YouTubeMeta YouTube { get; set; } = new YouTubeMeta();
    }
}
