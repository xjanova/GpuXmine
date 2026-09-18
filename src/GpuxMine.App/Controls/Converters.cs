using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GpuxMine.App.Controls;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Fraction 0–1 → width in a fixed-width container, for the hand-drawn bars.</summary>
public sealed class FractionToWidth : IValueConverter
{
    public double Total { get; set; } = 100;
    public object Convert(object value, Type t, object p, CultureInfo c)
        => Math.Clamp(value is double d ? d : 0, 0, 1) * (p is string s && double.TryParse(s, out var w) ? w : Total);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Schedule cell state → brush: share / cheap / mine.</summary>
public sealed class CellStateToBrush : IValueConverter
{
    public Brush? Share { get; set; }
    public Brush? Cheap { get; set; }
    public Brush? Mine { get; set; }
    public object? Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        "share" => Share,
        "cheap" => Cheap,
        _ => Mine,
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ChannelToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        "auto" or "job" => new SolidColorBrush(Color.FromRgb(0x7f, 0xe0, 0x7a)),
        "warn" or "therm" => new SolidColorBrush(Color.FromRgb(0xff, 0xae, 0x6a)),
        "pay" => new SolidColorBrush(Color.FromRgb(0xff, 0xd7, 0x7a)),
        _ => new SolidColorBrush(Color.FromRgb(0xcf, 0xe4, 0xff)),
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolInvert : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class EqualsToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Equals(value?.ToString(), p?.ToString());
    public object? ConvertBack(object value, Type t, object p, CultureInfo c) => value is true ? p?.ToString() : Binding.DoNothing;
}
