using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ComportMonitor;

/// <summary>별칭이 있고(HasAlias) 표시 옵션이 켜져 있을 때만 pill을 보여준다.</summary>
public class AliasVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasAlias = values.Length > 0 && values[0] is true;
        bool show = values.Length > 1 && values[1] is true;
        return hasAlias && show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
