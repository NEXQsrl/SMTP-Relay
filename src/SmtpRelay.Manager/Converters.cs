using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SmtpRelay.Manager;

/// <summary>Visible when the bound value equals the converter parameter (compared as text).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the bound value equals the converter parameter; used by the sidebar radio buttons.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter! : Binding.DoNothing;
}

/// <summary>Visible when true. With the parameter "invert", visible when false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (parameter is "invert")
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps a state name ("Ok", "Dirty", "Error", "Off") to a color. Parameter "soft" gives the background tint.</summary>
public sealed class KindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool soft = parameter is "soft";
        var key = value?.ToString() switch
        {
            "Ok" => soft ? "OkSoft" : "Ok",
            "Dirty" => soft ? "WarnSoft" : "Warn",
            "Error" => soft ? "DangerSoft" : "Danger",
            _ => soft ? "Page" : "Off"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
