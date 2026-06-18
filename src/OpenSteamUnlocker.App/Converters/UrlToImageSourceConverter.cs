using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace OpenSteamUnlocker.App.Converters;

/// <summary>
/// Converts an HTTP/HTTPS URL string to a <see cref="BitmapImage"/> suitable
/// for use as <c>Image.Source</c>. WPF downloads the image asynchronously when
/// the source URI is absolute, so this converter does not block the UI thread.
/// </summary>
[ValueConversion(typeof(string), typeof(BitmapImage))]
public sealed class UrlToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        try { return new BitmapImage(new Uri(url, UriKind.Absolute)); }
        catch { return null; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
