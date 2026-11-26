using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ProPS1Studio.App;

/// <summary>Converts a bool to "valid" / "expired" text for certificate display.</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "valid" : "expired";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a bool to the success/danger brush so validity is color-coded.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? Application.Current?.TryFindResource("SuccessBrush") as Brush
            : Application.Current?.TryFindResource("DangerBrush") as Brush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
