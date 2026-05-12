using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CheckerDSO.ViewModels;

namespace CheckerDSO.Views
{
    public partial class MainWindow : Window
    {
        private bool _captchaOpen = false;
        private bool _brightOpen  = false;

        public MainWindow()
        {
            InitializeComponent();

            // PasswordBox MVVM ilə bind edilmir, event ilə sync edirik
            BrightDataPasswordBox.PasswordChanged += (s, e) =>
            {
                if (DataContext is MainViewModel vm)
                    vm.BrightDataPassword = BrightDataPasswordBox.Password;
            };

            // Maximize/Restore ikonunu güncəllə
            StateChanged += (s, e) =>
            {
                MaxIcon.Data = WindowState == WindowState.Maximized
                    ? (System.Windows.Media.Geometry)FindResource("Ico_Restore")
                    : (System.Windows.Media.Geometry)FindResource("Ico_Max");
            };
        }

        // ─── CAPTCHA accordion toggle ───
        private void CaptchaHeader_Toggle(object sender, MouseButtonEventArgs e)
        {
            _captchaOpen = !_captchaOpen;
            CaptchaContent.Visibility = _captchaOpen ? Visibility.Visible : Visibility.Collapsed;
            AnimateChevron(CaptchaChevronRotate, _captchaOpen ? 180 : 0);
        }

        // ─── BrightData accordion toggle ───
        private void BrightHeader_Toggle(object sender, MouseButtonEventArgs e)
        {
            _brightOpen = !_brightOpen;
            BrightContent.Visibility = _brightOpen ? Visibility.Visible : Visibility.Collapsed;
            AnimateChevron(BrightChevronRotate, _brightOpen ? 180 : 0);
        }

        private static void AnimateChevron(RotateTransform rt, double toAngle)
        {
            var anim = new DoubleAnimation(toAngle, new Duration(System.TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            rt.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        private void MinimizeClick(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeClick(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseClick(object sender, RoutedEventArgs e)
            => Close();
    }
}
