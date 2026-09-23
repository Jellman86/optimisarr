using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Tray;

/// <summary>Decodes only the local worker's small, bounded JPEG and keeps the pipe's bytes off the UI.</summary>
public sealed class PreviewImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 and <= MonitorProtocol.MaximumPreviewBytes } jpeg) return null;
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
