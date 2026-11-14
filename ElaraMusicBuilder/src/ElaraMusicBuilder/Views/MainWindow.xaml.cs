using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ElaraMusicBuilder.Models;
using ElaraMusicBuilder.Services;
using Microsoft.Win32;

namespace ElaraMusicBuilder.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel VM = new MainViewModel();
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly MediaPlayer _player = new MediaPlayer();
        private readonly DispatcherTimer _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        private readonly DispatcherTimer _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        private readonly DispatcherTimer _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly SemaphoreSlim _measureGate = new SemaphoreSlim(3);

        private Timeline _timeline = new Timeline();
        private CancellationTokenSource _busyCts, _previewCts;
        private bool _previewRendering, _previewPending, _suppressDirty, _suppressSlider, _placing;
        private DateTime _lastPlayRender = DateTime.MinValue;

        // Воспроизведение: либо собранный альбом, либо отдельный трек со смещением на шкале альбома
        private bool _playAlbum;
        private int _playTrackIndex = -1;
        private double _playOffset, _pendingSeek;

        private ProjectModel _hooked;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = VM;

            _previewDebounce.Tick += (s, e) => { _previewDebounce.Stop(); _ = RenderPreviewAsync(); };
            _autosave.Tick += (s, e) => AutosaveNow();
            _playTimer.Tick += PlayTimer_Tick;
            _player.MediaOpened += Player_MediaOpened;
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaFailed += (s, e) => { StopPlayback(); VM.StatusText = "Не удалось воспроизвести файл: " + e.ErrorException?.Message; };

            StateChanged += (s, e) =>
            {
                RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            };
            Loaded += OnLoaded;
            Closing += OnClosing;

            var p = new ProjectModel();
            p.Export.OutputFolder = _settings.LastOutputFolder ?? AppPaths.DefaultOutput;
            SetProject(p, null, false);
        }

        // =====================================================================
        //  Запуск, закрытие, автосохранение
        // =====================================================================

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            foreach (var r in _settings.RecentProjects) VM.RecentProjects.Add(r);

            if (!FfmpegService.IsAvailable)
            {
                VM.FfmpegInfo = "Модуль FFmpeg не найден.";
                MessageBox.Show(this, "Не найден встроенный модуль обработки видео (FFmpeg).\nПереустановите программу с помощью ElaraMusicBuilder_Setup.exe.",
                    "Elara Music Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                VM.FfmpegInfo = await Task.Run(() => FfmpegService.VersionText());
                _ = Task.Run(() => FfmpegService.DetectGpuEncoder()).ContinueWith(t =>
                {
                    var enc = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    VM.EncoderInfo = enc == null
                        ? "Видеокарта для кодирования не найдена — будет использован процессор (x264)."
                        : "Доступно ускорение видеокартой: " + FfmpegService.EncoderDisplayName(enc) + ". Если что-то пойдёт не так, программа сама переключится на процессор.";
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            var fonts = await Task.Run(() => FontCatalog.All());
            VM.Fonts = fonts;
            SyncFontEntries();

            // Восстановление после сбоя
            if (!_settings.CleanExit && File.Exists(AppPaths.AutosaveFile))
            {
                var res = MessageBox.Show(this, "Похоже, программа в прошлый раз закрылась неожиданно.\nВосстановить последний автоматически сохранённый проект?",
                    "Восстановление проекта", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes) LoadProjectFromFile(AppPaths.AutosaveFile, true);
            }
            _settings.CleanExit = false;
            _settings.Save();
            _autosave.Start();

            if (!_settings.WelcomeShown)
            {
                _settings.WelcomeShown = true;
                _settings.Save();
                ShowWelcome();
            }

            SchedulePreview();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (VM.IsBusy)
            {
                var r = MessageBox.Show(this, "Сейчас идёт обработка. Прервать её и закрыть программу?", "Elara Music Builder", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
                _busyCts?.Cancel();
            }
            if (VM.IsDirty)
            {
                var r = MessageBox.Show(this, "Сохранить изменения в проекте перед выходом?", "Elara Music Builder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (r == MessageBoxResult.Yes && !SaveProject(false)) { e.Cancel = true; return; }
            }
            StopPlayback();
            _player.Close();
            _settings.CleanExit = true;
            _settings.Save();
            Log.Info("Программа закрыта");
        }

        private void AutosaveNow()
        {
            if (!VM.IsDirty) return;
            try { ProjectStore.Save(VM.Project, AppPaths.AutosaveFile); }
            catch (Exception ex) { Log.Warn("Автосохранение: " + ex.Message); }
        }

        public void EmergencySave()
        {
            try { ProjectStore.Save(VM.Project, AppPaths.AutosaveFile); } catch { }
        }

        private void ShowWelcome()
        {
            var w = new WelcomeWindow { Owner = this };
            w.ShowDialog();
            if (w.OpenDemo) DemoProject_Click(this, new RoutedEventArgs());
        }

        // =====================================================================
        //  Проект и отслеживание изменений
        // =====================================================================

        private void SetProject(ProjectModel p, string path, bool dirty)
        {
            StopPlayback();
            _suppressDirty = true;
            HookProject(p);
            VM.Project = p;
            VM.ProjectPath = path;
            if (string.IsNullOrEmpty(p.Export.OutputFolder)) p.Export.OutputFolder = _settings.LastOutputFolder ?? AppPaths.DefaultOutput;
            SyncFontEntries();
            VM.PreviewTime = 0;
            RefreshTimeline();
            UpdateBackgroundInfo();
            _suppressDirty = false;
            VM.IsDirty = dirty;
            foreach (var t in p.Tracks) EnsureMeasured(t);
            SchedulePreview();
        }

        private void HookProject(ProjectModel p)
        {
            if (_hooked != null)
            {
                _hooked.Audio.PropertyChanged -= Audio_Changed;
                _hooked.Background.PropertyChanged -= Visual_Changed;
                _hooked.Visualizer.PropertyChanged -= Visual_Changed;
                _hooked.Overlay.PropertyChanged -= Visual_Changed;
                _hooked.Export.PropertyChanged -= Other_Changed;
                _hooked.YouTube.PropertyChanged -= Other_Changed;
                _hooked.Tracks.CollectionChanged -= Tracks_CollectionChanged;
                foreach (var t in _hooked.Tracks) t.PropertyChanged -= Track_Changed;
            }
            _hooked = p;
            p.Audio.PropertyChanged += Audio_Changed;
            p.Background.PropertyChanged += Visual_Changed;
            p.Visualizer.PropertyChanged += Visual_Changed;
            p.Overlay.PropertyChanged += Visual_Changed;
            p.Export.PropertyChanged += Other_Changed;
            p.YouTube.PropertyChanged += Other_Changed;
            p.Tracks.CollectionChanged += Tracks_CollectionChanged;
            foreach (var t in p.Tracks) t.PropertyChanged += Track_Changed;
        }

        private void MarkDirty()
        {
            if (!_suppressDirty) VM.IsDirty = true;
        }

        private void Audio_Changed(object s, PropertyChangedEventArgs e) { MarkDirty(); RefreshTimeline(); SchedulePreview(); }
        private void Other_Changed(object s, PropertyChangedEventArgs e) { MarkDirty(); }

        private void Visual_Changed(object s, PropertyChangedEventArgs e)
        {
            MarkDirty();
            if (s is BackgroundSettings && e.PropertyName == nameof(BackgroundSettings.FilePath)) UpdateBackgroundInfo();
            SchedulePreview();
        }

        private void Track_Changed(object s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TrackItem.Number) || e.PropertyName == nameof(TrackItem.IsMeasuring) || e.PropertyName == nameof(TrackItem.IsMissing)) return;
            MarkDirty();
            if (e.PropertyName == nameof(TrackItem.Duration) || e.PropertyName == nameof(TrackItem.Title))
            {
                RefreshTimeline();
                SchedulePreview();
            }
        }

        private void Tracks_CollectionChanged(object s, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (TrackItem t in e.NewItems) t.PropertyChanged += Track_Changed;
            if (e.OldItems != null) foreach (TrackItem t in e.OldItems) t.PropertyChanged -= Track_Changed;
            MarkDirty();
            RefreshTimeline();
            SchedulePreview();
        }

        private void RefreshTimeline()
        {
            var p = VM.Project;
            for (int i = 0; i < p.Tracks.Count; i++) p.Tracks[i].Number = i + 1;
            _timeline = Timeline.Compute(p.Tracks, p.Audio);
            if (p.Tracks.Count == 0)
                VM.TimestampsText = "Добавьте песни — здесь появятся тайм-коды для YouTube.";
            else if (!_timeline.IsComplete)
                VM.TimestampsText = "Измеряю точную длительность песен…";
            else
                VM.TimestampsText = _timeline.ChaptersText();

            string word = Plural(p.Tracks.Count, "песня", "песни", "песен");
            VM.TotalText = p.Tracks.Count == 0 ? "" : $"{p.Tracks.Count} {word} · {Fmt.Time(_timeline.Total)}";
            _suppressSlider = true;
            VM.PreviewTotal = Math.Max(1, _timeline.Total);
            if (VM.PreviewTime > VM.PreviewTotal) VM.PreviewTime = 0;
            _suppressSlider = false;
            UpdateTrackLabel();
        }

        private static string Plural(int n, string one, string few, string many)
        {
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return one;
            if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
            return many;
        }

        private void UpdateTrackLabel()
        {
            if (_timeline.Entries.Count == 0) { VM.PreviewTrackLabel = "нет песен"; return; }
            int i = _timeline.IndexAt(VM.PreviewTime);
            VM.PreviewTrackLabel = $"{i + 1} / {_timeline.Entries.Count} — {_timeline.Entries[i].Title}";
        }

        private void SyncFontEntries()
        {
            if (VM.Fonts == null || VM.Fonts.Count == 0) return;
            var o = VM.Project.Overlay;
            bool old = _suppressDirty;
            _suppressDirty = true;
            try
            {
                string tPath = SafeResolve(o.TitleFont, true), bPath = SafeResolve(o.BodyFont, false);
                VM.TitleFontEntry = VM.Fonts.FirstOrDefault(f => string.Equals(f.Path, tPath, StringComparison.OrdinalIgnoreCase)) ?? VM.Fonts.FirstOrDefault();
                VM.BodyFontEntry = VM.Fonts.FirstOrDefault(f => string.Equals(f.Path, bPath, StringComparison.OrdinalIgnoreCase)) ?? VM.Fonts.FirstOrDefault();
            }
            finally { _suppressDirty = old; }
        }

        private static string SafeResolve(string path, bool title)
        {
            try { return FontCatalog.Resolve(path, title); } catch { return null; }
        }

        private void UpdateBackgroundInfo()
        {
            var bg = VM.Project.Background;
            if (string.IsNullOrEmpty(bg.FilePath)) { VM.BackgroundInfo = "Без фона будет использован тёмный однотонный фон."; return; }
            if (!File.Exists(bg.FilePath)) { VM.BackgroundInfo = "Файл не найден: " + bg.FilePath; return; }
            if (!FfmpegService.IsAvailable) { VM.BackgroundInfo = ""; return; }
            var info = MediaProbe.ProbeVideo(bg.FilePath);
            if (bg.IsVideo)
                VM.BackgroundInfo = $"Видео {info.Width}×{info.Height}, длительность {Fmt.Time(info.Duration)} — будет зациклено на всю длину музыки.";
            else
            {
                string warn = info.Height > 0 && info.Height < 1080 ? " Для Full HD лучше изображение не меньше 1920×1080." : "";
                VM.BackgroundInfo = $"Изображение {info.Width}×{info.Height}.{warn}";
            }
        }

        private static ProjectModel Clone(ProjectModel p)
        {
            // Снимок проекта для фоновой обработки: изменения в интерфейсе во время экспорта его не затронут
            var opts = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            return JsonSerializer.Deserialize<ProjectModel>(JsonSerializer.Serialize(p, opts), opts);
        }

        // =====================================================================
        //  Окно
        // =====================================================================

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // =====================================================================
        //  Шаг 1. Музыка
        // =====================================================================

        private void AddTracks_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = MediaTypes.AudioFilter, Multiselect = true, Title = "Выберите песни" };
            if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
        }

        private void FileDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void AudioDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            var list = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) list.AddRange(Directory.EnumerateFiles(p).Where(MediaTypes.IsAudio).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
                else list.Add(p);
            }
            int skipped = 0, added = 0;
            foreach (var f in list)
            {
                if (!MediaTypes.IsAudio(f) || !File.Exists(f)) { skipped++; continue; }
                var fi = new FileInfo(f);
                var t = new TrackItem
                {
                    FilePath = f,
                    Title = CleanTitle(Path.GetFileNameWithoutExtension(f)),
                    Format = MediaProbe.AudioFormatLabel(f),
                    FileSize = fi.Length,
                    FileTimeUtc = fi.LastWriteTimeUtc
                };
                VM.Project.Tracks.Add(t);
                EnsureMeasured(t);
                added++;
            }
            VM.StatusText = added > 0 ? $"Добавлено песен: {added}" : VM.StatusText;
            if (skipped > 0)
                MessageBox.Show(this, $"Пропущено файлов: {skipped}. Поддерживаются MP3, WAV, FLAC и M4A.", "Добавление музыки", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string CleanTitle(string name)
        {
            var s = name.Replace('_', ' ').Trim();
            // «01 - Название» → «Название»
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^\d{1,3}\s*[-.)]\s*(.+)$");
            if (m.Success) s = m.Groups[1].Value.Trim();
            return s;
        }

        /// <summary>Измеряет точную длительность трека (если файл новый или изменился).</summary>
        private async void EnsureMeasured(TrackItem t)
        {
            if (!File.Exists(t.FilePath)) { t.IsMissing = true; return; }
            t.IsMissing = false;
            var fi = new FileInfo(t.FilePath);
            if (t.Duration > 0 && fi.Length == t.FileSize && fi.LastWriteTimeUtc == t.FileTimeUtc) return;
            if (!FfmpegService.IsAvailable) return;
            t.FileSize = fi.Length;
            t.FileTimeUtc = fi.LastWriteTimeUtc;
            t.IsMeasuring = true;
            await _measureGate.WaitAsync();
            try
            {
                t.Duration = await MediaProbe.MeasureAudioSecondsAsync(t.FilePath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось измерить " + t.FilePath, ex);
                t.IsMissing = true;
                VM.StatusText = "Не удалось прочитать файл: " + Path.GetFileName(t.FilePath);
            }
            finally
            {
                _measureGate.Release();
                t.IsMeasuring = false;
                RefreshTimeline();
            }
        }

        private void SortTracks_Click(object sender, RoutedEventArgs e)
        {
            var sorted = VM.Project.Tracks.OrderBy(t => Path.GetFileName(t.FilePath), StringComparer.CurrentCultureIgnoreCase).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int cur = VM.Project.Tracks.IndexOf(sorted[i]);
                if (cur != i) VM.Project.Tracks.Move(cur, i);
            }
            VM.StatusText = "Треки отсортированы по имени файла";
        }

        private void ClearTracks_Click(object sender, RoutedEventArgs e)
        {
            if (VM.Project.Tracks.Count == 0) return;
            if (MessageBox.Show(this, "Убрать все песни из списка? Сами файлы на диске не удаляются.", "Очистить трек-лист", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            StopPlayback();
            VM.Project.Tracks.Clear();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TrackItem t)
            {
                int i = VM.Project.Tracks.IndexOf(t);
                if (i > 0) VM.Project.Tracks.Move(i, i - 1);
            }
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TrackItem t)
            {
                int i = VM.Project.Tracks.IndexOf(t);
                if (i >= 0 && i < VM.Project.Tracks.Count - 1) VM.Project.Tracks.Move(i, i + 1);
            }
        }

        private void RemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TrackItem t)
            {
                StopPlayback();
                VM.Project.Tracks.Remove(t);
            }
        }

        // ---- Перетаскивание треков для изменения порядка ----
        private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TrackItem t)
            {
                DragDrop.DoDragDrop(TrackList, new DataObject(typeof(TrackItem), t), DragDropEffects.Move);
                e.Handled = true;
            }
        }

        private void TrackList_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            else if (e.Data.GetDataPresent(typeof(TrackItem))) e.Effects = DragDropEffects.Move;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void TrackList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { AddFiles(files); return; }
            if (!(e.Data.GetData(typeof(TrackItem)) is TrackItem dragged)) return;
            var tracks = VM.Project.Tracks;
            int from = tracks.IndexOf(dragged);
            if (from < 0) return;
            var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as TrackItem;
            int to = target != null ? tracks.IndexOf(target) : tracks.Count - 1;
            if (to >= 0 && to != from) tracks.Move(from, to);
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // ---- Тайм-коды и экспорт альбома ----
        private bool CheckTimelineReady()
        {
            if (VM.Project.Tracks.Count == 0) { Info("Сначала добавьте песни."); return false; }
            if (!_timeline.IsComplete) { Info("Длительность некоторых песен ещё измеряется. Подождите несколько секунд."); return false; }
            return true;
        }

        private void CopyTimestamps_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckTimelineReady()) return;
            TrySetClipboard(_timeline.ChaptersText());
            VM.StatusText = "Тайм-коды скопированы в буфер обмена";
        }

        private void SaveTimestamps_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckTimelineReady()) return;
            var dlg = new SaveFileDialog { Filter = "Текст (*.txt)|*.txt", FileName = VideoExporter.SafeFileName(VM.Project.Export.FileName) + "_тайм-коды.txt" };
            if (dlg.ShowDialog(this) != true) return;
            File.WriteAllText(dlg.FileName, _timeline.ChaptersText(), new UTF8Encoding(true));
            VM.StatusText = "Тайм-коды сохранены: " + dlg.FileName;
        }

        private void ExportAlbumMp3_Click(object sender, RoutedEventArgs e) => ExportAlbum(".mp3");
        private void ExportAlbumWav_Click(object sender, RoutedEventArgs e) => ExportAlbum(".wav");

        private async void ExportAlbum(string ext)
        {
            if (!CheckTimelineReady() || !CheckSourcesExist()) return;
            var dlg = new SaveFileDialog
            {
                Filter = ext == ".mp3" ? "MP3 (*.mp3)|*.mp3" : "WAV (*.wav)|*.wav",
                FileName = VideoExporter.SafeFileName(VM.Project.Export.FileName) + ext,
                InitialDirectory = VM.Project.Export.OutputFolder
            };
            if (dlg.ShowDialog(this) != true) return;
            var snapshot = Clone(VM.Project);
            await RunBusy("Сборка альбома…", async (progress, ct) =>
            {
                var album = await AudioAlbumBuilder.BuildAsync(snapshot, new Progress<(double, string)>(x => progress.Report((x.Item1 * 0.8, x.Item2))), ct);
                await AudioAlbumBuilder.ExportAlbumAsync(album.WavPath, dlg.FileName, album.Timeline.Total,
                    new Progress<(double, string)>(x => progress.Report((0.8 + x.Item1 * 0.2, x.Item2))), ct);
                VM.StatusText = "Альбом сохранён: " + dlg.FileName;
                Info("Альбом сохранён:\n" + dlg.FileName + (album.Warning != null ? "\n\n" + album.Warning : ""));
            });
        }

        // =====================================================================
        //  Шаг 2. Фон
        // =====================================================================

        private void ChooseBackground_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = MediaTypes.BackgroundFilter, Title = "Выберите фон" };
            if (dlg.ShowDialog(this) == true) SetBackground(dlg.FileName);
        }

        private void BackgroundDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) SetBackground(files[0]);
        }

        private void SetBackground(string path)
        {
            if (!MediaTypes.IsImage(path) && !MediaTypes.IsVideo(path))
            {
                Info("Этот формат не поддерживается. Используйте JPG, PNG, WEBP, MP4 или MOV.");
                return;
            }
            VM.Project.Background.FilePath = path;
        }

        private void RemoveBackground_Click(object sender, RoutedEventArgs e) => VM.Project.Background.FilePath = null;

        // =====================================================================
        //  Шаг 3. Эквалайзер
        // =====================================================================

        private void VizColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string hex) VM.Project.Visualizer.Color = hex;
        }

        private void VizCustomColor_Click(object sender, RoutedEventArgs e)
        {
            var c = PickColor(VM.Project.Visualizer.Color);
            if (c != null) VM.Project.Visualizer.Color = c;
        }

        private static string PickColor(string current)
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (ColorUtil.TryParse(current, out var r, out var g, out var b)) dlg.Color = System.Drawing.Color.FromArgb(r, g, b);
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            return $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        }

        // =====================================================================
        //  Шаг 4. Оформление
        // =====================================================================

        private void ImportFont_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = MediaTypes.FontFilter, Title = "Выберите файл шрифта" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var entry = FontCatalog.Import(dlg.FileName);
                VM.Fonts = FontCatalog.All();
                VM.TitleFontEntry = VM.Fonts.FirstOrDefault(f => f.Path == entry.Path) ?? entry;
                Info($"Шрифт «{entry.Name}» добавлен и выбран для заголовков.\nЕго также можно выбрать для треклиста.");
            }
            catch (Exception ex) { Error("Не удалось импортировать шрифт.", ex); }
        }

        private void PickTextColor_Click(object sender, RoutedEventArgs e)
        {
            var o = VM.Project.Overlay;
            string tag = (sender as FrameworkElement)?.Tag as string;
            string cur = tag == "title" ? o.TitleColor : tag == "text" ? o.TextColor : o.HighlightColor;
            var c = PickColor(cur);
            if (c == null) return;
            if (tag == "title") o.TitleColor = c;
            else if (tag == "text") o.TextColor = c;
            else o.HighlightColor = c;
        }

        private void ChooseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = MediaTypes.ImageFilter, Title = "Выберите логотип (лучше PNG с прозрачностью)" };
            if (dlg.ShowDialog(this) != true) return;
            VM.Project.Overlay.LogoPath = dlg.FileName;
            VM.Project.Overlay.ShowLogo = true;
        }

        private void RemoveLogo_Click(object sender, RoutedEventArgs e) => VM.Project.Overlay.LogoPath = null;

        // =====================================================================
        //  Предпросмотр
        // =====================================================================

        private void SchedulePreview()
        {
            if (!IsLoaded) return;
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }

        private async Task RenderPreviewAsync()
        {
            if (!FfmpegService.IsAvailable) return;
            if (_previewRendering) { _previewPending = true; return; }
            _previewRendering = true;
            VM.PreviewLoading = true;
            _previewCts = new CancellationTokenSource();
            string dir = null;
            try
            {
                var png = await VideoExporter.RenderPreviewFrameAsync(VM.Project, _timeline, VM.PreviewTime, _previewCts.Token);
                dir = Path.GetDirectoryName(png);
                var bytes = await File.ReadAllBytesAsync(png);
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                VM.PreviewImage = bmp;
                VM.PreviewError = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Предпросмотр", ex);
                var first = (ex.Message ?? "").Split('\n').FirstOrDefault();
                VM.PreviewError = "Кадр не построен: " + first;
            }
            finally
            {
                if (dir != null) AppPaths.TryDeleteDir(dir);
                _previewRendering = false;
                VM.PreviewLoading = false;
                if (_previewPending) { _previewPending = false; SchedulePreview(); }
            }
        }

        private void RefreshPreview_Click(object sender, RoutedEventArgs e) => _ = RenderPreviewAsync();

        private void PreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSlider) return;
            UpdateTrackLabel();
            if (VM.IsPlaying && Mouse.LeftButton == MouseButtonState.Pressed) SeekPlayback(VM.PreviewTime);
            SchedulePreview();
        }

        private void PreviewSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (VM.IsPlaying) SeekPlayback(VM.PreviewTime);
        }

        private void PrevSong_Click(object sender, RoutedEventArgs e) => JumpSong(-1);
        private void NextSong_Click(object sender, RoutedEventArgs e) => JumpSong(+1);

        private void JumpSong(int dir)
        {
            if (_timeline.Entries.Count == 0) return;
            int i = Math.Clamp(_timeline.IndexAt(VM.PreviewTime) + dir, 0, _timeline.Entries.Count - 1);
            var en = _timeline.Entries[i];
            VM.PreviewTime = Math.Min(en.End - 0.1, en.Start + Math.Min(5, en.Duration / 2));
            if (VM.IsPlaying) SeekPlayback(VM.PreviewTime);
        }

        // ---- Перемещение элементов кликом по кадру ----
        private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _placing = true;
            PreviewSurface.CaptureMouse();
            ApplyPlace(e.GetPosition(PreviewSurface));
        }

        private void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (_placing && e.LeftButton == MouseButtonState.Pressed) ApplyPlace(e.GetPosition(PreviewSurface));
        }

        private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_placing) return;
            _placing = false;
            PreviewSurface.ReleaseMouseCapture();
            SchedulePreview();
        }

        private void ApplyPlace(Point pt)
        {
            double px = Math.Clamp(pt.X / 1280.0 * 100, 0, 100), py = Math.Clamp(pt.Y / 720.0 * 100, 0, 100);
            Canvas.SetLeft(PlaceMarker, pt.X - 13);
            Canvas.SetTop(PlaceMarker, pt.Y - 13);
            PlaceMarker.Visibility = Visibility.Visible;
            var p = VM.Project;
            switch (VM.PlaceTarget)
            {
                case PlaceTarget.Visualizer:
                    p.Visualizer.X = Math.Clamp(px - p.Visualizer.Width / 2, 0, 100);
                    p.Visualizer.Y = Math.Clamp(py - p.Visualizer.Height / 2, 0, 100);
                    break;
                case PlaceTarget.Title: p.Overlay.TitleX = px; p.Overlay.TitleY = py; break;
                case PlaceTarget.Tracklist: p.Overlay.TracklistX = px; p.Overlay.TracklistY = py; break;
                case PlaceTarget.CurrentSong: p.Overlay.CurrentX = px; p.Overlay.CurrentY = py; break;
                case PlaceTarget.Logo: p.Overlay.LogoX = Math.Clamp(px - p.Overlay.LogoWidth / 2, 0, 100); p.Overlay.LogoY = py; break;
                case PlaceTarget.CustomText: p.Overlay.CustomX = px; p.Overlay.CustomY = py; break;
            }
        }

        // ---- Воспроизведение ----
        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (VM.IsPlaying) { StopPlayback(); return; }
            StartPlayback(VM.PreviewTime);
        }

        private void PlayTrack_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is TrackItem t)) return;
            int i = VM.Project.Tracks.IndexOf(t);
            if (i < 0 || i >= _timeline.Entries.Count) return;
            StopPlayback();
            _suppressSlider = true;
            VM.PreviewTime = _timeline.Entries[i].Start;
            _suppressSlider = false;
            UpdateTrackLabel();
            StartPlayback(VM.PreviewTime);
            SchedulePreview();
        }

        private void StartPlayback(double t)
        {
            if (VM.Project.Tracks.Count == 0 || _timeline.Entries.Count == 0) return;
            if (AudioAlbumBuilder.IsCached(VM.Project))
            {
                _playAlbum = true;
                _playOffset = 0;
                _pendingSeek = t;
                _player.Open(new Uri(AudioAlbumBuilder.CachedPath(VM.Project)));
            }
            else
            {
                _playAlbum = false;
                _playTrackIndex = _timeline.IndexAt(t);
                var en = _timeline.Entries[_playTrackIndex];
                _playOffset = en.Start;
                _pendingSeek = Math.Max(0, t - en.Start);
                var path = VM.Project.Tracks[_playTrackIndex].FilePath;
                if (!File.Exists(path)) { Info("Файл песни не найден:\n" + path); return; }
                _player.Open(new Uri(path));
            }
            VM.IsPlaying = true;
            _playTimer.Start();
        }

        private void Player_MediaOpened(object sender, EventArgs e)
        {
            _player.Position = TimeSpan.FromSeconds(_pendingSeek);
            _player.Play();
        }

        private void Player_MediaEnded(object sender, EventArgs e)
        {
            if (!_playAlbum && _playTrackIndex + 1 < _timeline.Entries.Count)
            {
                _playTrackIndex++;
                var en = _timeline.Entries[_playTrackIndex];
                _playOffset = en.Start;
                _pendingSeek = 0;
                _player.Open(new Uri(VM.Project.Tracks[_playTrackIndex].FilePath));
                return;
            }
            StopPlayback();
        }

        private void SeekPlayback(double t)
        {
            if (!VM.IsPlaying) return;
            if (_playAlbum) { _player.Position = TimeSpan.FromSeconds(t); return; }
            int i = _timeline.IndexAt(t);
            if (i == _playTrackIndex) _player.Position = TimeSpan.FromSeconds(Math.Max(0, t - _playOffset));
            else { StopPlayback(); StartPlayback(t); }
        }

        private void StopPlayback()
        {
            _playTimer.Stop();
            try { _player.Stop(); } catch { }
            VM.IsPlaying = false;
        }

        private void PlayTimer_Tick(object sender, EventArgs e)
        {
            if (!VM.IsPlaying || Mouse.LeftButton == MouseButtonState.Pressed) return;
            double t = _playOffset + _player.Position.TotalSeconds;
            _suppressSlider = true;
            VM.PreviewTime = Math.Min(t, VM.PreviewTotal);
            _suppressSlider = false;
            UpdateTrackLabel();
            if (!_previewRendering && (DateTime.Now - _lastPlayRender).TotalSeconds > 2)
            {
                _lastPlayRender = DateTime.Now;
                _ = RenderPreviewAsync();
            }
        }

        // =====================================================================
        //  Шаг 5. Создание видео
        // =====================================================================

        private bool CheckSourcesExist()
        {
            var missing = ProjectStore.FindMissing(VM.Project);
            if (missing.Count == 0) return true;
            MessageBox.Show(this, "Не найдены файлы проекта:\n\n" + string.Join("\n", missing.Take(15)) +
                                  (missing.Count > 15 ? $"\n…и ещё {missing.Count - 15}" : "") +
                                  "\n\nВерните файлы на место или замените их в проекте.",
                "Отсутствуют файлы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private async void CreateVideo_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckTimelineReady() || !CheckSourcesExist()) return;
            StopPlayback();
            var snapshot = Clone(VM.Project);
            await RunBusy("Создание видео…", async (progress, ct) =>
            {
                var outcome = await VideoExporter.ExportAsync(snapshot, false, 0, progress, ct);
                VM.LastOutputPath = outcome.OutputPath;
                if (string.IsNullOrWhiteSpace(VM.Project.YouTube.Description))
                    YouTubeTextBuilder.Generate(VM.Project, outcome.Timeline);
                VM.StatusText = "Видео готово: " + outcome.OutputPath;
                var sb = new StringBuilder();
                sb.AppendLine("Видео создано и проверено.");
                sb.AppendLine();
                sb.AppendLine(outcome.OutputPath);
                sb.AppendLine($"Длительность: {Fmt.Time(outcome.Duration)} · Кодирование: {outcome.EncoderUsed}");
                foreach (var n in outcome.Notes) sb.AppendLine("• " + n);
                sb.AppendLine();
                sb.Append("Открыть папку с видео?");
                if (MessageBox.Show(this, sb.ToString(), "Готово", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenOutputFolder_Click(this, new RoutedEventArgs());
            });
        }

        private async void TestVideo_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckTimelineReady() || !CheckSourcesExist()) return;
            StopPlayback();
            double start = VM.PreviewTime;
            var snapshot = Clone(VM.Project);
            await RunBusy("Тестовое видео…", async (progress, ct) =>
            {
                var outcome = await VideoExporter.ExportAsync(snapshot, true, start, progress, ct);
                VM.LastOutputPath = outcome.OutputPath;
                VM.StatusText = "Тестовое видео готово: " + outcome.OutputPath;
                var notes = outcome.Notes.Count > 0 ? "\n\n• " + string.Join("\n• ", outcome.Notes) : "";
                if (MessageBox.Show(this, $"Тестовое видео (20 секунд с {Fmt.Time(start)}) готово:\n{outcome.OutputPath}{notes}\n\nОткрыть его?",
                        "Тестовое видео", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenLastVideo_Click(this, new RoutedEventArgs());
            });
        }

        private async Task RunBusy(string title, Func<IProgress<(double, string)>, CancellationToken, Task> work)
        {
            if (VM.IsBusy) return;
            VM.IsBusy = true;
            VM.ProgressValue = 0;
            VM.ProgressText = title;
            _busyCts = new CancellationTokenSource();
            var progress = new Progress<(double frac, string msg)>(x =>
            {
                VM.ProgressValue = Math.Clamp(x.frac * 100, 0, 100);
                if (!string.IsNullOrEmpty(x.msg)) VM.ProgressText = $"{x.msg}  ({VM.ProgressValue:0} %)";
            });
            try
            {
                await work(progress, _busyCts.Token);
            }
            catch (OperationCanceledException)
            {
                VM.StatusText = "Операция отменена. Незавершённый файл удалён.";
            }
            catch (Exception ex)
            {
                Error("Не удалось выполнить операцию.", ex);
                VM.StatusText = "Ошибка: " + ex.Message.Split('\n').FirstOrDefault();
            }
            finally
            {
                VM.IsBusy = false;
                _busyCts = null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _busyCts?.Cancel();
            VM.ProgressText = "Отмена…";
        }

        private void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Папка для готовых видео", InitialDirectory = VM.Project.Export.OutputFolder };
            if (dlg.ShowDialog(this) != true) return;
            VM.Project.Export.OutputFolder = dlg.FolderName;
            _settings.LastOutputFolder = dlg.FolderName;
            _settings.Save();
        }

        private void OpenLastVideo_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(VM.LastOutputPath) || !File.Exists(VM.LastOutputPath)) return;
            try { Process.Start(new ProcessStartInfo(VM.LastOutputPath) { UseShellExecute = true }); }
            catch (Exception ex) { Error("Не удалось открыть видео.", ex); }
        }

        private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(VM.LastOutputPath) && File.Exists(VM.LastOutputPath))
                    Process.Start("explorer.exe", "/select,\"" + VM.LastOutputPath + "\"");
                else
                    Process.Start(new ProcessStartInfo(VM.Project.Export.OutputFolder ?? AppPaths.DefaultOutput) { UseShellExecute = true });
            }
            catch (Exception ex) { Error("Не удалось открыть папку.", ex); }
        }

        // ---- YouTube ----
        private void GenerateYouTube_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckTimelineReady()) return;
            YouTubeTextBuilder.Generate(VM.Project, _timeline);
            VM.StatusText = "Описание для YouTube создано — проверьте и отредактируйте его.";
        }

        private void CopyDescription_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VM.Project.YouTube.Description))
            {
                if (!CheckTimelineReady()) return;
                YouTubeTextBuilder.Generate(VM.Project, _timeline);
            }
            TrySetClipboard(VM.Project.YouTube.Description);
            VM.StatusText = "Описание скопировано в буфер обмена";
        }

        private void SaveDescription_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VM.Project.YouTube.Description))
            {
                if (!CheckTimelineReady()) return;
                YouTubeTextBuilder.Generate(VM.Project, _timeline);
            }
            var dlg = new SaveFileDialog { Filter = "Текст (*.txt)|*.txt", FileName = VideoExporter.SafeFileName(VM.Project.Export.FileName) + "_YouTube.txt" };
            if (dlg.ShowDialog(this) != true) return;
            File.WriteAllText(dlg.FileName, YouTubeTextBuilder.FullText(VM.Project), new UTF8Encoding(true));
            VM.StatusText = "Описание сохранено: " + dlg.FileName;
        }

        private void OpenThumbnail_Click(object sender, RoutedEventArgs e)
        {
            var w = new ThumbnailWindow(VM.Project) { Owner = this };
            w.ShowDialog();
        }

        // =====================================================================
        //  Проекты
        // =====================================================================

        private bool ConfirmDiscard()
        {
            if (!VM.IsDirty) return true;
            var r = MessageBox.Show(this, "Сохранить изменения в текущем проекте?", "Elara Music Builder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return SaveProject(false);
            return true;
        }

        private void NewProject_Click(object sender, RoutedEventArgs e)
        {
            if (VM.IsBusy || !ConfirmDiscard()) return;
            var p = new ProjectModel();
            p.Export.OutputFolder = _settings.LastOutputFolder ?? AppPaths.DefaultOutput;
            SetProject(p, null, false);
            VM.Page = 0;
            VM.Step = 1;
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            if (VM.IsBusy || !ConfirmDiscard()) return;
            var dlg = new OpenFileDialog { Filter = MediaTypes.ProjectFilter, Title = "Открыть проект" };
            if (dlg.ShowDialog(this) == true) LoadProjectFromFile(dlg.FileName, false);
        }

        private void Recent_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (RecentList.SelectedItem is string path)
            {
                if (VM.IsBusy || !ConfirmDiscard()) return;
                if (!File.Exists(path)) { Info("Файл проекта не найден:\n" + path); return; }
                LoadProjectFromFile(path, false);
            }
        }

        private void LoadProjectFromFile(string path, bool fromAutosave)
        {
            try
            {
                var p = ProjectStore.Load(path);
                var missing = ProjectStore.FindMissing(p);
                SetProject(p, fromAutosave ? null : path, fromAutosave);
                if (!fromAutosave) AddRecent(path);
                VM.Page = 0;
                VM.StatusText = "Проект открыт: " + Path.GetFileName(path);
                if (missing.Count > 0)
                    MessageBox.Show(this, "Некоторые файлы проекта не найдены (возможно, перемещены или удалены):\n\n" +
                                          string.Join("\n", missing.Take(15)) + (missing.Count > 15 ? $"\n…и ещё {missing.Count - 15}" : ""),
                        "Отсутствуют файлы", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { Error("Не удалось открыть проект. Возможно, файл повреждён.", ex); }
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(false);
        private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProject(true);

        private bool SaveProject(bool saveAs)
        {
            string path = VM.ProjectPath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                var dlg = new SaveFileDialog { Filter = MediaTypes.ProjectFilter, FileName = VideoExporter.SafeFileName(VM.Project.Overlay.AlbumTitle ?? "Мой альбом") + ".elara" };
                if (dlg.ShowDialog(this) != true) return false;
                path = dlg.FileName;
            }
            try
            {
                VM.Project.Name = Path.GetFileNameWithoutExtension(path);
                ProjectStore.Save(VM.Project, path);
                VM.ProjectPath = path;
                VM.IsDirty = false;
                AddRecent(path);
                VM.StatusText = "Проект сохранён: " + path;
                return true;
            }
            catch (Exception ex) { Error("Не удалось сохранить проект.", ex); return false; }
        }

        private void AddRecent(string path)
        {
            _settings.AddRecent(path);
            VM.RecentProjects.Clear();
            foreach (var r in _settings.RecentProjects) VM.RecentProjects.Add(r);
        }

        private async void DemoProject_Click(object sender, RoutedEventArgs e)
        {
            if (VM.IsBusy || !ConfirmDiscard()) return;
            await RunBusy("Создаю демо-проект…", async (progress, ct) =>
            {
                var p = await DemoProject.CreateAsync(ct);
                p.Export.OutputFolder = _settings.LastOutputFolder ?? AppPaths.DefaultOutput;
                SetProject(p, null, false);
                VM.Page = 0;
                VM.Step = 1;
                VM.StatusText = "Демо-проект открыт. Нажмите «Тест 20 сек» или «Создать видео».";
            });
        }

        // =====================================================================
        //  Настройки
        // =====================================================================

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true }); } catch { }
        }

        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            if (VM.IsBusy) { Info("Дождитесь окончания обработки."); return; }
            StopPlayback();
            _player.Close();
            long freed = 0;
            foreach (var f in Directory.GetFiles(AppPaths.Cache))
            {
                try { freed += new FileInfo(f).Length; File.Delete(f); } catch { }
            }
            Info("Кэш очищен. Освобождено: " + Fmt.Bytes(freed));
        }

        private void ShowWelcome_Click(object sender, RoutedEventArgs e) => ShowWelcome();

        // =====================================================================
        //  Сообщения
        // =====================================================================

        private void Info(string text) => MessageBox.Show(this, text, "Elara Music Builder", MessageBoxButton.OK, MessageBoxImage.Information);

        private void Error(string text, Exception ex)
        {
            Log.Error(text, ex);
            MessageBox.Show(this, text + "\n\n" + ex.Message + "\n\nПодробности записаны в журнал (Настройки → Открыть папку журналов).",
                "Elara Music Builder", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void TrySetClipboard(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text ?? ""); return; }
                catch { Thread.Sleep(60); }
            }
            Info("Буфер обмена занят другой программой. Попробуйте ещё раз.");
        }
    }

    /// <summary>Русские названия для списка «Перемещать кликом».</summary>
    public class PlaceTargetNames : IValueConverter
    {
        public static readonly PlaceTargetNames Instance = new PlaceTargetNames();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            PlaceTarget.Visualizer => "Эквалайзер",
            PlaceTarget.Title => "Название и артист",
            PlaceTarget.Tracklist => "Треклист",
            PlaceTarget.CurrentSong => "Текущая песня",
            PlaceTarget.Logo => "Логотип",
            PlaceTarget.CustomText => "Свой текст",
            _ => value?.ToString()
        };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
