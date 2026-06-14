using System;
using System.Globalization;
using System.Reflection;
using System.Windows.Data;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace AutoTabOrganiser.UI.Converters
{
    /// <summary>
    /// Converts a moniker name (string) to a <see cref="ImageMoniker"/> looked up on the
    /// <see cref="KnownMonikers"/> static catalogue. Empty/unknown names return
    /// <see cref="default"/> so the bound CrispImage simply renders nothing.
    /// </summary>
    internal sealed class StringToImageMonikerConverter : IValueConverter
    {
        private static readonly Type KnownMonikersType = typeof(KnownMonikers);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value as string;
            if (string.IsNullOrEmpty(name)) return default(ImageMoniker);
            var prop = KnownMonikersType.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return default(ImageMoniker);
            try { return (ImageMoniker)prop.GetValue(null); }
            catch { return default(ImageMoniker); }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Visibility converter: visible iff the bound <see cref="bool"/> is true. Used to hide
    /// optional row pieces (subtitle, tag chips, status icons).
    /// </summary>
    internal sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Visibility converter: visible iff the bound string is non-empty. Used to hide the
    /// toolbar version label (and its divider) when the version can't be read.
    /// </summary>
    internal sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value as string) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
