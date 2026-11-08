using System.IO;
using System.Windows.Media.Imaging;

namespace IdeviceGui.Services.Imaging;

/// <summary>
/// 网格图标加载（M4-b2）：只读 <c>data\icons\&lt;IconKey&gt;.png</c>（扫描时已缩到档位的缓存），
/// 解码后 **`Freeze()`** 再交给界面——冻结的位图可跨线程复用、不占 UI 线程解码（docs/01 §6.2 / §6.4）。
/// <para>文件不存在 / 解码失败返回 null（界面显示占位，不抛、不影响行数据）。</para>
/// </summary>
internal static class IconImageLoader
{
    /// <summary>解码宽度：显示 32 px，按 2× 取 64 保证高 DPI 不清（docs/06 §3.1）。</summary>
    public const int DecodePixelWidth = 64;

    public static object? Load(string dataDirectory, string iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey)) return null;
        try
        {
            var path = Path.Combine(IconCachePaths.CacheDirectory(dataDirectory), iconKey + ".png");
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;      // 关流后仍可用
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DecodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // 坏图 / 被占用：当作没有图标
            return null;
        }
    }
}
