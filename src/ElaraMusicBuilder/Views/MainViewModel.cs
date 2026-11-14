using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ElaraMusicBuilder.Models;
using ElaraMusicBuilder.Services;

namespace ElaraMusicBuilder.Views
{
    /// <summary>Состояние главного окна. Логика действий — в MainWindow.xaml.cs.</summary>
    public class MainViewModel : ObservableObject
    {
        private ProjectModel _project = new ProjectModel();
        private int _page, _step = 1;
        private string _timestamps = "", _totalText = "", _status = "Готово к работе";
        private bool _isBusy, _previewLoading, _isPlaying, _isDirty;
        private double _progress, _previewTime, _previewTotal = 1;
        private string _progressText = "", _previewTrackLabel = "", _previewError;
        private ImageSource _previewImage;
        private List<FontEntry> _fonts = new List<FontEntry>();
        private FontEntry _titleFont, _bodyFont;
        private string _encoderInfo = "Проверка…", _ffmpegInfo = "";
        private PlaceTarget _placeTarget = PlaceTarget.Visualizer;
        private string _lastOutput, _projectPath, _bgInfo = "";

        public ProjectModel Project { get => _project; set => Set(ref _project, value); }
        public int Page { get => _page; set => Set(ref _page, value); }
        public int Step { get => _step; set => Set(ref _step, value); }
        public string TimestampsText { get => _timestamps; set => Set(ref _timestamps, value); }
        public string TotalText { get => _totalText; set => Set(ref _totalText, value); }
        public string StatusText { get => _status; set => Set(ref _status, value); }

        public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); } }
        public bool IsIdle => !_isBusy;
        public double ProgressValue { get => _progress; set => Set(ref _progress, value); }
        public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

        public ImageSource PreviewImage { get => _previewImage; set => Set(ref _previewImage, value); }
        public bool PreviewLoading { get => _previewLoading; set => Set(ref _previewLoading, value); }
        public string PreviewError { get => _previewError; set => Set(ref _previewError, value); }
        public double PreviewTime
        {
            get => _previewTime;
            set { if (Set(ref _previewTime, value)) OnPropertyChanged(nameof(PreviewTimeText)); }
        }
        public double PreviewTotal
        {
            get => _previewTotal;
            set { if (Set(ref _previewTotal, value)) OnPropertyChanged(nameof(PreviewTotalText)); }
        }
        public string PreviewTimeText => Fmt.Time(_previewTime);
        public string PreviewTotalText => Fmt.Time(_previewTotal);
        public string PreviewTrackLabel { get => _previewTrackLabel; set => Set(ref _previewTrackLabel, value); }
        public bool IsPlaying { get => _isPlaying; set { if (Set(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayGlyph)); } }
        public string PlayGlyph => _isPlaying ? "" : "";

        public List<FontEntry> Fonts { get => _fonts; set => Set(ref _fonts, value); }
        public FontEntry TitleFontEntry
        {
            get => _titleFont;
            set { if (Set(ref _titleFont, value) && value != null) Project.Overlay.TitleFont = value.Path; }
        }
        public FontEntry BodyFontEntry
        {
            get => _bodyFont;
            set { if (Set(ref _bodyFont, value) && value != null) Project.Overlay.BodyFont = value.Path; }
        }

        public string EncoderInfo { get => _encoderInfo; set => Set(ref _encoderInfo, value); }
        public string FfmpegInfo { get => _ffmpegInfo; set => Set(ref _ffmpegInfo, value); }
        public PlaceTarget PlaceTarget { get => _placeTarget; set => Set(ref _placeTarget, value); }
        public string LastOutputPath { get => _lastOutput; set => Set(ref _lastOutput, value); }
        public string ProjectPath { get => _projectPath; set { if (Set(ref _projectPath, value)) OnPropertyChanged(nameof(ProjectTitle)); } }
        public bool IsDirty { get => _isDirty; set { if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(ProjectTitle)); } }
        public string ProjectTitle => (string.IsNullOrEmpty(_projectPath) ? "Новый проект" : System.IO.Path.GetFileNameWithoutExtension(_projectPath)) + (_isDirty ? " •" : "");
        public string BackgroundInfo { get => _bgInfo; set => Set(ref _bgInfo, value); }

        public ObservableCollection<string> RecentProjects { get; } = new ObservableCollection<string>();
    }
}
