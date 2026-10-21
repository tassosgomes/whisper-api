using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Whisper.Recorder.Host;

/// <summary>Esconde o elemento quando o valor é nulo ou vazio.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
