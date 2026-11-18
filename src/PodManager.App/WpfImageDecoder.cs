using System.Windows.Media;
using System.Windows.Media.Imaging;
using PodManager.Core;

namespace PodManager.App;

/// <summary>Decodes cover art with WPF's codecs (JPEG, PNG, BMP), scaled while decoding.</summary>
public sealed class WpfImageDecoder : IImageDecoder
{
    public RgbImage? Decode(byte[] data, int maxSide)
    {
        using var ms = new MemoryStream(data);
        var probe = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int w = probe.PixelWidth, h = probe.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        if (w >= h && w > maxSide) img.DecodePixelWidth = maxSide;
        else if (h > w && h > maxSide) img.DecodePixelHeight = maxSide;
        img.EndInit();
        img.Freeze();

        var rgb = new FormatConvertedBitmap(img, PixelFormats.Rgb24, null, 0);
        int stride = rgb.PixelWidth * 3;
        var pixels = new byte[stride * rgb.PixelHeight];
        rgb.CopyPixels(pixels, stride, 0);
        return new RgbImage(rgb.PixelWidth, rgb.PixelHeight, pixels);
    }
}
