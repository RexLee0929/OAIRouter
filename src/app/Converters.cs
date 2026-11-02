using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OAIRouter;

/// <summary>true → Visible，false → Collapsed</summary>
public sealed class BoolToVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>false → Visible，true → Collapsed</summary>
public sealed class InverseBoolToVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>空字符串 → Collapsed，非空 → Visible</summary>
public sealed class StringToVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>日志级别 → 文字颜色</summary>
public sealed class LevelToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => new System.Windows.Media.SolidColorBrush(Accent.Error),
        LogLevel.Warn => new System.Windows.Media.SolidColorBrush(Accent.Warn),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5C, 0x5C, 0x5C)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
