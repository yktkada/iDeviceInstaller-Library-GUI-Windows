using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdeviceGui.Services.Parsing;

/// <summary>
/// JPEG 图标的兜底解码（部分 <c>iTunesArtwork</c> 是 JPEG，自写 PngCodec 不解 JPEG）。
/// 用 WPF/WIC 解码后统一转成 RGBA 交给 <see cref="PngCodec"/> 缩放与重编码。
/// <para>失败返回 null（调用方按"用默认图标"处理，不影响 ParseState）。</para>
/// </summary>
internal static class WpfImageDecoder
{
    public static PngCodec.DecodedPng? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource source = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            var stride = source.PixelWidth * 4;
            var buffer = new byte[stride * source.PixelHeight];
            source.CopyPixels(buffer, stride, 0);

            // BGRA32 → RGBA8888
            for (var i = 0; i < buffer.Length; i += 4)
                (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);

            return new PngCodec.DecodedPng(source.PixelWidth, source.PixelHeight, buffer);
        }
        catch
        {
            return null;
        }
    }
}
