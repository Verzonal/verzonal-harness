using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Dsh.App;

/// <summary>Shows an element while a flag is set.</summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Whether to show the element when the flag is <em>clear</em> instead.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool set && set;
        return flag != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => (value is Visibility.Visible) != Invert;
}

/// <summary>Shows an element while a value is present.</summary>
public sealed partial class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Whether to show the element when the value is <em>absent</em> instead.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is not null) != Invert ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Shows an element while a string has something in it.</summary>
public sealed partial class TextToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Renders a 0-to-1 fraction as a percentage for a meter's label.</summary>
public sealed partial class PercentConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is double fraction ? $"{fraction * 100:0}%" : string.Empty;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
