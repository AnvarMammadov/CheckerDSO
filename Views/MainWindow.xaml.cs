using System.Windows;
using System.Windows.Controls;
using CheckerDSO.ViewModels;

namespace CheckerDSO.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // PasswordBox MVVM ile bind edilemiyor, event ile sync ediyoruz
            BrightDataPasswordBox.PasswordChanged += (s, e) =>
            {
                if (DataContext is MainViewModel vm)
                    vm.BrightDataPassword = BrightDataPasswordBox.Password;
            };
        }
    }
}
