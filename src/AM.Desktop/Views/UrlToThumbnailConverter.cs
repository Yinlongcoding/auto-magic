using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace AM.Desktop.Views;

/// <summary>
/// 图片地址 → 缩略图。按参数给的像素宽度解码，几千张 1688 原图也不会占满内存。
/// 地址为空或无效时返回 null，界面显示占位底色。
/// </summary>
public sealed class UrlToThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // 1688 的地址可能不带协议
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var width = int.TryParse(parameter as string, out var w) ? w : 96;

        // 同一张图在多处出现（颜色图被各尺码共用、滚动时容器回收重建），按地址和宽度复用，不重复下载解码。
        // 缩略图很小，满了直接清空，不做 LRU。只在 UI 线程上调用，不用加锁。
        var key = (url, width);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.DecodePixelWidth = width;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DownloadFailed += (_, _) => Cache.Remove(key);
        image.EndInit();
        Cache[key] = image;
        return image;
    }

    private const int CacheLimit = 2000;

    private static readonly Dictionary<(string Url, int Width), BitmapImage> Cache = [];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
