using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Patchboard.ViewModels;

/// <summary>Visible when true, Collapsed when false. Collapsed rather than Hidden so cells reflow.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Visible when false. For "show this only when there is nothing".</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

/// <summary>
/// A "#RRGGBB" string to a brush, falling back to the parameter or the default surface.
/// Returns the fallback rather than throwing on a malformed value, because the colour comes
/// from a hand editable config file.
/// </summary>
public sealed class ColorStringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                if (ColorConverter.ConvertFromString(text) is Color color)
                    return new SolidColorBrush(color);
            }
            catch (FormatException)
            {
                // Fall through to the default below.
            }
        }

        return Application.Current?.TryFindResource("SurfaceBrush") as Brush
               ?? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// A 0..1 level to a pixel width, scaled by the track width passed as the second binding.
/// Used for the device level meters, where a plain ProgressBar would carry more chrome
/// than a two pixel bar needs.
/// </summary>
public sealed class LevelToWidthConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not float level || values[1] is not double trackWidth)
            return 0d;

        if (double.IsNaN(trackWidth) || trackWidth <= 0) return 0d;

        // Meters read better with a mild curve: a linear meter spends most of its travel
        // in a range that quiet speech never reaches, so it looks dead.
        var shaped = Math.Sqrt(Math.Clamp(level, 0f, 1f));
        return shaped * trackWidth;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Works out how tall a tile must be for exactly N rows to fill the visible area.
///
/// This is what makes the rows setting mean what a user expects. A UniformGrid given both
/// Rows and Columns silently drops every item past rows times columns, which would hide
/// sounds with no explanation. Setting only the column count and sizing the tiles instead
/// keeps every sound reachable: the requested number of rows fills the window and the rest
/// scrolls.
/// </summary>
public sealed class GridTileHeightConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double viewportHeight || values[1] is not int rows)
            return double.NaN;

        if (double.IsNaN(viewportHeight) || viewportHeight <= 0 || rows <= 0) return double.NaN;

        // 6 is the tile's own 3px margin on each side.
        var height = viewportHeight / rows - 6;
        return height < 40 ? 40d : height;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the string is non-empty. For hiding empty labels and tags.</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
