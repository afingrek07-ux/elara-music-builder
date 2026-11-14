using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ElaraMusicBuilder.Views
{
    /// <summary>Строка настройки: подпись, ползунок и значение.</summary>
    public partial class SliderRow : UserControl
    {
        public SliderRow()
        {
            InitializeComponent();
            LayoutRoot.DataContext = this;
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderRow));
        public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SliderRow));
        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SliderRow), new PropertyMetadata(0.0));
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SliderRow), new PropertyMetadata(100.0));
        public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(SliderRow), new PropertyMetadata(1.0));
        public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(SliderRow), new PropertyMetadata("", OnDisplayChanged));
        public static readonly DependencyProperty MultiplierProperty = DependencyProperty.Register(nameof(Multiplier), typeof(double), typeof(SliderRow), new PropertyMetadata(1.0, OnDisplayChanged));
        public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(SliderRow), new PropertyMetadata(0, OnDisplayChanged));
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SliderRow),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDisplayChanged));
        private static readonly DependencyPropertyKey DisplayTextKey = DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(SliderRow), new PropertyMetadata(""));
        public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;

        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
        public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
        public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
        public double Multiplier { get => (double)GetValue(MultiplierProperty); set => SetValue(MultiplierProperty, value); }
        public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public string DisplayText => (string)GetValue(DisplayTextProperty);

        private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var s = (SliderRow)d;
            double v = s.Value * s.Multiplier;
            string fmt = s.Decimals <= 0 ? "0" : "0." + new string('0', s.Decimals);
            s.SetValue(DisplayTextKey, v.ToString(fmt, CultureInfo.GetCultureInfo("ru-RU")) + s.Suffix);
        }
    }
}
