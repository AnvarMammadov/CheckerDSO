using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CheckerDSO.Models;

namespace CheckerDSO.Converters
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is AccountStatus status)
            {
                return status switch
                {
                    AccountStatus.Pending => new SolidColorBrush(Colors.Gray),
                    AccountStatus.Checking => new SolidColorBrush(Colors.LightBlue),
                    AccountStatus.Verified => new SolidColorBrush(Colors.Green),
                    AccountStatus.Unverified => new SolidColorBrush(Colors.Gold), // Sarı
                    AccountStatus.WrongPass => new SolidColorBrush(Colors.Red),
                    AccountStatus.Error => new SolidColorBrush(Colors.DarkGray),
                    AccountStatus.Captcha => new SolidColorBrush(Colors.Orange),
                    _ => new SolidColorBrush(Colors.Transparent),
                };
            }
            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
