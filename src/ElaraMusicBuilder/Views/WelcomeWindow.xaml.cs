using System.Windows;
using System.Windows.Input;

namespace ElaraMusicBuilder.Views
{
    public partial class WelcomeWindow : Window
    {
        public bool OpenDemo { get; private set; }

        public WelcomeWindow()
        {
            InitializeComponent();
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }

        private void Demo_Click(object sender, RoutedEventArgs e)
        {
            OpenDemo = true;
            Close();
        }

        private void Start_Click(object sender, RoutedEventArgs e) => Close();
    }
}
