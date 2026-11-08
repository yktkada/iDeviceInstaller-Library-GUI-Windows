using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace IdeviceGui.Services.Parsing;

/// <summary>
/// 自写的 PNG 编解码（零依赖、可单测）。M3 用它做两件事：
/// ① 解 CgBI（Apple 私有 PNG）：IDAT 是**裸 deflate**，行过滤器是标准 PNG 的，
///    像素是 BGRA + alpha 预乘 → 解出来再还原成 RGBA；
/// ② 把图标缩到缓存档位后**重新编码成标准 PNG** 落盘。
/// <para>
/// 为什么不用 WIC/WPF 解码：核心逻辑要能脱离 UI 线程与 STA 单测（`dotnet test` 跑在 MTA 上）。
/// JPEG（部分 iTunesArtwork）不走这里，由上层用 WPF 解码器兜底。
/// </para>
/// </summary>
internal static class PngCodec
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    internal sealed record DecodedPng(int Width, int Height, byte[] Rgba);

    /// <summary>是否是 PNG（含 CgBI）。</summary>
    public static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(Signature);

    /// <summary>是否是 CgBI（第一个 chunk 是 CgBI）。</summary>
    public static bool IsCgbi(ReadOnlySpan<byte> data) =>
        IsPng(data) && data.Length >= 16 && Encoding.ASCII.GetString(data[12..16]) == "CgBI";

    /// <summary>解码 PNG / CgBI 到 RGBA8888。只支持 8 位、非隔行、color type 2/6。</summary>
    public static DecodedPng Decode(byte[] data)
    {
        if (!IsPng(data)) throw new InvalidDataException("不是有效的 PNG");
        var cgbi = IsCgbi(data);

        var offset = 8;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var interlace = 0;
        byte[]? ihdr = null;
        using var idat = new MemoryStream();

        var done = false;
        while (!done && offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            if (length < 0 || offset + 12 + length > data.Length) throw new InvalidDataException("PNG chunk 越界");
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var body = data.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    ihdr = body.ToArray();
                    width = BinaryPrimitives.ReadInt32BigEndian(body[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..8]);
                    bitDepth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    done = true;
                    break;
            }

            offset += 12 + length;
        }

        if (ihdr is null) throw new InvalidDataException("PNG 缺少 IHDR");
        if (bitDepth != 8) throw new NotSupportedException($"不支持此 PNG 位深： {bitDepth} ");
        if (colorType is not (2 or 6)) throw new NotSupportedException($"不支持此 PNG：colorType {colorType} ");
        if (interlace != 0) throw new NotSupportedException("不支持隔行 PNG");

        var channels = colorType == 6 ? 4 : 3;
        var stride = width * channels;
        var raw = Inflate(idat.ToArray(), cgbi);
        if (raw.Length < height * (stride + 1))
            throw new InvalidDataException($"PNG 数据长度不符：{raw.Length} < {height * (stride + 1)}");

        var pixels = Unfilter(raw, width, height, channels);
        var rgba = ToRgba(pixels, width, height, channels, cgbi);
        return new DecodedPng(width, height, rgba);
    }

    private static byte[] Inflate(byte[] compressed, bool cgbi)
    {
        // 标准 PNG：zlib 头（0x78...）；CgBI：裸 deflate
        if (!cgbi && compressed.Length >= 2 && compressed[0] == 0x78)
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }

        using var rawInput = new MemoryStream(compressed);
        using var deflate = new DeflateStream(rawInput, CompressionMode.Decompress);
        using var rawOutput = new MemoryStream();
        deflate.CopyTo(rawOutput);
        return rawOutput.ToArray();
    }

    /// <summary>按 PNG 规范反过滤（filter type 0–4）。</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        var stride = width * channels;
        var output = new byte[height * stride];
        var bpp = channels;
        var pos = 0;

        for (var y = 0; y < height; y++)
        {
            var filter = raw[pos++];
            var rowStart = y * stride;
            var prevStart = (y - 1) * stride;
            switch (filter)
            {
                case 0:
                    Buffer.BlockCopy(raw, pos, output, rowStart, stride);
                    break;
                case 1:
                    for (var x = 0; x < stride; x++)
                    {
                        var left = x >= bpp ? output[rowStart + x - bpp] : (byte)0;
                        output[rowStart + x] = (byte)(raw[pos + x] + left);
                    }

                    break;
                case 2:
                    for (var x = 0; x < stride; x++)
                    {
                        var up = y > 0 ? output[prevStart + x] : (byte)0;
                        output[rowStart + x] = (byte)(raw[pos + x] + up);
                    }

                    break;
                case 3:
                    for (var x = 0; x < stride; x++)
                    {
                        var left = x >= bpp ? output[rowStart + x - bpp] : 0;
                        var up = y > 0 ? output[prevStart + x] : 0;
                        output[rowStart + x] = (byte)(raw[pos + x] + ((left + up) >> 1));
                    }

                    break;
                case 4:
                    for (var x = 0; x < stride; x++)
                    {
                        var left = x >= bpp ? output[rowStart + x - bpp] : 0;
                        var up = y > 0 ? output[prevStart + x] : 0;
                        var upLeft = x >= bpp && y > 0 ? output[prevStart + x - bpp] : 0;
                        output[rowStart + x] = (byte)(raw[pos + x] + Paeth(left, up, upLeft));
                    }

                    break;
                default:
                    throw new InvalidDataException("未知 PNG filter " + filter);
            }

            pos += stride;
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static byte[] ToRgba(byte[] pixels, int width, int height, int channels, bool cgbi)
    {
        var count = width * height;
        var rgba = new byte[count * 4];
        if (channels == 3)
        {
            for (var i = 0; i < count; i++)
            {
                var s = i * 3;
                var d = i * 4;
                if (cgbi)
                {
                    // CgBI 是 BGRA，但 colorType 2 不带 alpha
                    rgba[d] = pixels[s + 2];
                    rgba[d + 1] = pixels[s + 1];
                    rgba[d + 2] = pixels[s];
                }
                else
                {
                    rgba[d] = pixels[s];
                    rgba[d + 1] = pixels[s + 1];
                    rgba[d + 2] = pixels[s + 2];
                }

                rgba[d + 3] = 255;
            }

            return rgba;
        }

        for (var i = 0; i < count; i++)
        {
            var s = i * 4;
            var d = i * 4;
            if (!cgbi)
            {
                Buffer.BlockCopy(pixels, s, rgba, d, 4);
                continue;
            }

            // CgBI：BGRA + 预乘 alpha → RGBA 反预乘
            int b = pixels[s], g = pixels[s + 1], r = pixels[s + 2], a = pixels[s + 3];
            if (a == 0)
            {
                rgba[d] = rgba[d + 1] = rgba[d + 2] = 0;
            }
            else if (a == 255)
            {
                rgba[d] = (byte)r;
                rgba[d + 1] = (byte)g;
                rgba[d + 2] = (byte)b;
            }
            else
            {
                rgba[d] = (byte)Math.Min(255, (r * 255 + a / 2) / a);
                rgba[d + 1] = (byte)Math.Min(255, (g * 255 + a / 2) / a);
                rgba[d + 2] = (byte)Math.Min(255, (b * 255 + a / 2) / a);
            }

            rgba[d + 3] = (byte)a;
        }

        return rgba;
    }

    /// <summary>
    /// 盒式（面积平均）缩放到目标尺寸；只缩小，放大直接返回原图（避免糊）。
    /// </summary>
    public static DecodedPng Downscale(DecodedPng source, int targetPx)
    {
        if (targetPx <= 0) return source;

        // 最长边缩到 target，另一边等比（图标基本是方形，非方形也保持比例）
        int width, height;
        if (source.Width >= source.Height)
        {
            width = targetPx;
            height = Math.Max(1, (int)Math.Round((double)source.Height * targetPx / source.Width));
        }
        else
        {
            height = targetPx;
            width = Math.Max(1, (int)Math.Round((double)source.Width * targetPx / source.Height));
        }

        if (source.Width <= width && source.Height <= height) return source;

        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var y0 = y * source.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * source.Height / height);
            for (var x = 0; x < width; x++)
            {
                var x0 = x * source.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * source.Width / width);
                // 在**预乘**空间求平均，否则透明/半透明边缘会发黑（彩色边）：
                // 全透明像素的 RGB 可能是 0，直接平均会把周围颜色拉暗。
                long r = 0, g = 0, b = 0, a = 0;
                var n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var rowBase = sy * source.Width * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = rowBase + sx * 4;
                        int alpha = source.Rgba[i + 3];
                        r += source.Rgba[i] * alpha;
                        g += source.Rgba[i + 1] * alpha;
                        b += source.Rgba[i + 2] * alpha;
                        a += alpha;
                        n++;
                    }
                }

                var d = (y * width + x) * 4;
                var alphaSum = a;
                if (alphaSum == 0)
                {
                    output[d] = output[d + 1] = output[d + 2] = output[d + 3] = 0;
                    continue;
                }

                output[d] = (byte)Math.Min(255, r / alphaSum);
                output[d + 1] = (byte)Math.Min(255, g / alphaSum);
                output[d + 2] = (byte)Math.Min(255, b / alphaSum);
                output[d + 3] = (byte)(alphaSum / n);
            }
        }

        return new DecodedPng(width, height, output);
    }

    /// <summary>把 RGBA 编码成标准 PNG（每行 filter 0，zlib 压缩）。</summary>
    public static byte[] Encode(DecodedPng image)
    {
        var stride = image.Width * 4;
        var raw = new byte[image.Height * (stride + 1)];
        for (var y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0;   // filter type 0
            Buffer.BlockCopy(image.Rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = ms.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), image.Height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // RGBA
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(body);
        var crc = Crc32(typeBytes, body);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] type, byte[] body)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = Update(crc, b);
        foreach (var b in body) crc = Update(crc, b);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return crc;
    }
}
