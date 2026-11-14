using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElaraMusicBuilder.Models;
using ElaraMusicBuilder.Services;
using Microsoft.Win32;

namespace ElaraMusicBuilder.Views
{
    /// <summary>Простой редактор обложки YouTube 1280×720 с сохранением в PNG/JPEG.</summary>
    public partial class ThumbnailWindow : Window
    {
        private bool _ready;
        private Color _c1 = Color.FromRgb(0x2A, 0x14, 0x30), _c2 = Color.FromRgb(0x0E, 0x0F, 0x1A);
        private Color _titleColor = Colors.White, _subColor = Color.FromRgb(0xF1, 0x7C, 0x8A), _shadeColor = Colors.Black;
        private bool _shadeVertical;
        private readonly ProjectModel _project;

        private UIElement _drag;
        private Point _dragStart;
        private double _origLeft, _origTop;

        public ThumbnailWindow(ProjectModel project)
        {
            InitializeComponent();
            _project = project;

            var families = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
            FontBox.ItemsSource = families;
            FontBox.DisplayMemberPath = "Source";
            FontBox.SelectedItem = families.FirstOrDefault(f => f.Source == "Georgia") ?? families.FirstOrDefault();

            TitleBox.Text = project.Overlay.AlbumTitle ?? "";
            SubBox.Text = project.Overlay.ArtistName ?? "";
            if (project.Background.IsImage && File.Exists(project.Background.FilePath)) Photo.Source = Load(project.Background.FilePath);
            if (!string.IsNullOrEmpty(project.Overlay.LogoPath) && File.Exists(project.Overlay.LogoPath)) LogoImg.Source = Load(project.Overlay.LogoPath);

            foreach (var row in new[] { PhotoScale, ShadeStrength, TitleSize, SubSize, LogoSize })
                DependencyPropertyDescriptor.FromProperty(SliderRow.ValueProperty, typeof(SliderRow)).AddValueChanged(row, (s, e) => UpdateArt());

            _ready = true;
            UpdateArt();
        }

        private static BitmapImage Load(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void Changed(object sender, RoutedEventArgs e) => UpdateArt();

        private void UpdateArt()
        {
            if (!_ready) return;
            BgRect.Fill = new LinearGradientBrush(_c1, _c2, new Point(0, 0), new Point(1, 1));
            double k = ShadeStrength.Value;
            var solid = Color.FromArgb((byte)(255 * k), _shadeColor.R, _shadeColor.G, _shadeColor.B);
            var clear = Color.FromArgb(0, _shadeColor.R, _shadeColor.G, _shadeColor.B);
            Shade.Fill = _shadeVertical
                ? new LinearGradientBrush(clear, solid, new Point(0, 0.35), new Point(0, 1))
                : new LinearGradientBrush(solid, clear, new Point(0, 0), new Point(0.85, 0));
            Shade.Visibility = GradientCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            var font = FontBox.SelectedItem as FontFamily ?? new FontFamily("Segoe UI");
            TitleText.Text = TitleBox.Text;
            TitleText.FontFamily = font;
            TitleText.FontSize = TitleSize.Value;
            TitleText.Foreground = new SolidColorBrush(_titleColor);
            SubText.Text = SubBox.Text;
            SubText.FontFamily = font;
            SubText.FontSize = SubSize.Value;
            SubText.Foreground = new SolidColorBrush(_subColor);

            double s = PhotoScale.Value;
            Photo.Width = 1280 * s;
            Photo.Height = 720 * s;
            LogoImg.Width = LogoSize.Value;
        }

        private void Template_Click(object sender, RoutedEventArgs e)
        {
            switch ((sender as FrameworkElement)?.Tag as string)
            {
                case "light":
                    _c1 = Color.FromRgb(0xF8, 0xF9, 0xFC); _c2 = Color.FromRgb(0xE9, 0xE6, 0xFB);
                    _titleColor = Color.FromRgb(0x25, 0x2B, 0x38); _subColor = Color.FromRgb(0x78, 0x68, 0xE6);
                    _shadeColor = Colors.White; _shadeVertical = false; break;
                case "neon":
                    _c1 = Color.FromRgb(0x12, 0x0A, 0x2A); _c2 = Color.FromRgb(0x2B, 0x0B, 0x3F);
                    _titleColor = Color.FromRgb(0xF7, 0xF3, 0xFF); _subColor = Color.FromRgb(0x45, 0xB8, 0xAC);
                    _shadeColor = Color.FromRgb(0x24, 0x0A, 0x40); _shadeVertical = false; break;
                case "cinema":
                    _c1 = Colors.Black; _c2 = Color.FromRgb(0x11, 0x11, 0x11);
                    _titleColor = Color.FromRgb(0xF1, 0xE6, 0xD2); _subColor = Color.FromRgb(0xE6, 0xB4, 0x50);
                    _shadeColor = Colors.Black; _shadeVertical = true; break;
                default:
                    _c1 = Color.FromRgb(0x2A, 0x14, 0x30); _c2 = Color.FromRgb(0x0E, 0x0F, 0x1A);
                    _titleColor = Colors.White; _subColor = Color.FromRgb(0xF1, 0x7C, 0x8A);
                    _shadeColor = Colors.Black; _shadeVertical = false; break;
            }
            UpdateArt();
        }

        private static Color? Pick(Color current)
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            return Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
        }

        private void PickBg_Click(object sender, RoutedEventArgs e)
        {
            bool first = ((sender as FrameworkElement)?.Tag as string) == "c1";
            var c = Pick(first ? _c1 : _c2);
            if (c == null) return;
            if (first) _c1 = c.Value; else _c2 = c.Value;
            UpdateArt();
        }

        private void PickText_Click(object sender, RoutedEventArgs e)
        {
            bool title = ((sender as FrameworkElement)?.Tag as string) == "title";
            var c = Pick(title ? _titleColor : _subColor);
            if (c == null) return;
            if (title) _titleColor = c.Value; else _subColor = c.Value;
            UpdateArt();
        }

        private void LoadPhoto_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = MediaTypes.ImageFilter };
            if (dlg.ShowDialog(this) != true) return;
            try { Photo.Source = Load(dlg.FileName); Canvas.SetLeft(Photo, 0); Canvas.SetTop(Photo, 0); }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть изображение: " + ex.Message, Title); }
        }

        private void RemovePhoto_Click(object sender, RoutedEventArgs e) => Photo.Source = null;

        private void LoadLogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = MediaTypes.ImageFilter };
            if (dlg.ShowDialog(this) != true) return;
            try { LogoImg.Source = Load(dlg.FileName); }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть логотип: " + ex.Message, Title); }
        }

        private void RemoveLogo_Click(object sender, RoutedEventArgs e) => LogoImg.Source = null;

        // ---- Перетаскивание элементов ----
        private void Element_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _drag = sender as UIElement;
            if (_drag == null) return;
            _dragStart = e.GetPosition(Art);
            _origLeft = double.IsNaN(Canvas.GetLeft(_drag)) ? 0 : Canvas.GetLeft(_drag);
            _origTop = double.IsNaN(Canvas.GetTop(_drag)) ? 0 : Canvas.GetTop(_drag);
            _drag.CaptureMouse();
            e.Handled = true;
        }

        private void Element_MouseMove(object sender, MouseEventArgs e)
        {
            if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(Art);
            Canvas.SetLeft(_drag, _origLeft + (p.X - _dragStart.X));
            Canvas.SetTop(_drag, _origTop + (p.Y - _dragStart.Y));
        }

        private void Element_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _drag?.ReleaseMouseCapture();
            _drag = null;
        }

        // ---- Сохранение ----
        private void SavePng_Click(object sender, RoutedEventArgs e) => Save(false);
        private void SaveJpg_Click(object sender, RoutedEventArgs e) => Save(true);

        private void Save(bool jpeg)
        {
            var dlg = new SaveFileDialog
            {
                Filter = jpeg ? "JPEG (*.jpg)|*.jpg" : "PNG (*.png)|*.png",
                FileName = VideoExporter.SafeFileName(_project.Export.FileName ?? "Elara") + "_обложка" + (jpeg ? ".jpg" : ".png")
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var rtb = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(Art);
                BitmapEncoder enc = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
                MessageBox.Show(this, "Обложка сохранена:\n" + dlg.FileName, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error("Сохранение обложки", ex);
                MessageBox.Show(this, "Не удалось сохранить обложку: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
