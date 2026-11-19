using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yuandl.ModbusAssistant.Converters;

/// <summary>
/// 整数比较转换器：当绑定值等于指定的 Parameter 时返回 Visible，否则返回 Collapsed
/// </summary>
public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter != null)
        {
            if (int.TryParse(parameter.ToString(), out int targetInt))
            {
                return intValue == targetInt ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
