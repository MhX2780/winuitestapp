using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace UGA;

/// <summary>
/// Converts ChatMessage.Role to Visibility.
/// Usage: ConverterParameter="user" or "model"
/// </summary>
public class RoleToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string role && parameter is string target)
        {
            if (target == "user" && role == "user")
                return Visibility.Visible;
            if (target == "model" && (role == "model" || role == "tool" || role == "system"))
                return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts bool to Visibility (true = Visible, false = Collapsed).
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
            return b ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts bool to Opacity (true = 1.0, false = 0.4).
/// </summary>
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
            return b ? 1.0 : 0.4;
        return 0.4;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
