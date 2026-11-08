using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdeviceGui.Services.Imaging;

namespace IdeviceGui.Services.Parsing;

/// <summary>一张候选图标的元数据（不解码）。</summary>
internal sealed record IpaIconEntry(
    string EntryName,
    int Width,
    int Height,
    bool IsCgbi,
    bool IsJpeg,
    long ByteLength,
    IconSource Source)
{
    public int MaxSide => Math.Max(Width, Height);
}

/// <summary>M3 深解析结果：浅字段 + 体积 + 架构 + 图标候选。</summary>
internal sealed class DeepParseResult
{
    public string BundleId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Build { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string MinOs { get; set; } = "";
    public string Sdk { get; set; } = "";
    public string DeviceFamily { get; set; } = "";
    public string RequiredCapsJson { get; set; } = "";
    public string AppDirectory { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Arch { get; set; } = "";

    public long CompressedSize { get; set; }
    public long UncompressedSize { get; set; }
    public long PayloadSize { get; set; }
    public long AppSize { get; set; }
    public long ExecutableSize { get; set; }
    public long FrameworksSize { get; set; }
    public long PlugInsSize { get; set; }

    public List<IpaIconEntry> IconCandidates { get; } = new();

    /// <summary>非致命问题（图标解不出来、未知魔数等），不影响 ParseState=Ok。</summary>
    public string? Warning { get; set; }

    /// <summary>致命错误（zip 坏 / 没有 Info.plist）→ ParseState=Failed。</summary>
    public string? Error { get; set; }

    /// <summary>M5-lite 适配证据 JSON（启动图尺寸 / storyboard / 图标尺寸）。</summary>
    public string LaunchImageJson { get; set; } = "";

    /// <summary>IPA 来源（PackageOrigin 常量；2026-11-05）。</summary>
    public string OriginKind { get; set; } = "";
}

/// <summary>图标解码结果（已还原成标准 PNG 字节 + 缓存键）。</summary>
internal sealed record IconLoadResult(byte[] PngBytes, string IconKey, int Width, int Height, bool UsedFallback = false)
{
    /// <summary>图标内容置空（只留键），用于把"该行引用了哪些图标"传出来做引用计数。</summary>
    public static IconLoadResult KeyOnly(string iconKey) => new(Array.Empty<byte>(), iconKey, 0, 0);
}

/// <summary>
/// M3 深解析：一次打开 zip，读出 Info.plist 深字段、体积 6 项、架构集合、图标候选，
/// 并按需把选中图标解码 / CgBI 还原 / 缩到档位 / 重编码成 PNG。
/// <para>口径见 docs/04：图标候选优先级 iTunesArtwork → plist 点名 → 根目录含 icon；
/// 全部要求近似方形且边长 ≥ 57（过滤启动图与 UI 素材）。</para>
/// </summary>
internal static class IpaDeepReader
{
    private const int MinIconSide = 57;

    public static DeepParseResult Read(string path)
    {
        var result = new DeepParseResult();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var infoEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".app/Info.plist", StringComparison.OrdinalIgnoreCase) &&
                CountSlashes(e.FullName) == 2);
            if (infoEntry is null)
            {
                result.Error = "Payload/*.app/Info.plist not found in package";
                return result;
            }

            var appDir = infoEntry.FullName.Split('/')[1];
            var appPrefix = "Payload/" + appDir + "/";
            result.AppDirectory = appDir;

            var plistBytes = ReadEntry(infoEntry);
            var dict = Plist.Parse(plistBytes) as Dictionary<string, object?>;

            result.BundleId = Str(dict, "CFBundleIdentifier");
            result.Build = Str(dict, "CFBundleVersion");
            result.Version = Str(dict, "CFBundleShortVersionString") is { Length: > 0 } v ? v : result.Build;
            result.DisplayName = Str(dict, "CFBundleDisplayName") is { Length: > 0 } dn
                ? dn
                : Str(dict, "CFBundleName") is { Length: > 0 } bn ? bn : appDir;
            result.MinOs = Str(dict, "MinimumOSVersion");
            result.Sdk = Str(dict, "DTSDKName");
            result.Executable = Str(dict, "CFBundleExecutable");
            result.DeviceFamily = FamilyText(dict);
            result.RequiredCapsJson = CapsJson(dict);

            var namedIcons = PlistIconNames(dict);
            var launchPrefixes = LaunchPrefixes(dict);
            var launchSizes = new List<(int Width, int Height)>();
            var iconSides = new HashSet<int>();
            AddPlistLaunchSizes(dict, launchSizes);
            var exeFullName = result.Executable.Length > 0 ? appPrefix + result.Executable : null;
            var hasEmbeddedProvision = false;
            var hasScInfo = false;

            // 一次遍历：体积累加 + 收集图标候选
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                result.CompressedSize += entry.CompressedLength;
                result.UncompressedSize += entry.Length;

                if (name.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase))
                {
                    result.PayloadSize += entry.Length;
                    if (name.StartsWith(appPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.AppSize += entry.Length;
                        if (name.StartsWith(appPrefix + "Frameworks/", StringComparison.OrdinalIgnoreCase))
                            result.FrameworksSize += entry.Length;
                        if (name.StartsWith(appPrefix + "PlugIns/", StringComparison.OrdinalIgnoreCase))
                            result.PlugInsSize += entry.Length;
                        if (exeFullName is not null && name.Equals(exeFullName, StringComparison.OrdinalIgnoreCase))
                            result.ExecutableSize = entry.Length;
                        if (name.Equals(appPrefix + "embedded.mobileprovision", StringComparison.OrdinalIgnoreCase))
                            hasEmbeddedProvision = true;
                        if (name.StartsWith(appPrefix + "SC_Info/", StringComparison.OrdinalIgnoreCase))
                            hasScInfo = true;
                    }
                }

                if (!IsImage(name)) continue;

                var (source, isArtwork) = ClassifyIcon(name, appPrefix, namedIcons);
                var isLaunch = IsLaunchName(name, appPrefix, launchPrefixes);
                if (source is null && !isLaunch) continue;

                var head = ReadHead(entry, 65536);   // JPEG 的 EXIF/APP 段可能很长，4 KB 读不到 SOF
                var (width, height, isCgbi, isJpeg) = ImageProbe.Probe(head);
                if (width <= 0 || height <= 0) continue;

                if (isLaunch) launchSizes.Add((width, height));
                if (source is not null && Math.Abs(width - height) <= Math.Max(2, (int)(Math.Max(width, height) * 0.02)))
                    iconSides.Add(width);

                if (source is null) continue;
                if (Math.Abs(width - height) > Math.Max(2, (int)(Math.Max(width, height) * 0.02))) continue;   // 非方形 → 素材
                if (width < MinIconSide) continue;                                                            // 太小 → 不是图标

                result.IconCandidates.Add(new IpaIconEntry(name, width, height, isCgbi, isJpeg, entry.Length,
                    isArtwork ? IconSource.StoreArtwork : source.Value));
            }

            // 架构：按 CFBundleExecutable 定位可执行文件，读前 4 KB
            if (exeFullName is not null)
            {
                var exeEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(exeFullName, StringComparison.OrdinalIgnoreCase));
                if (exeEntry is not null)
                    result.Arch = MachO.ReadArch(ReadHead(exeEntry, 4096));
                else
                    result.Warning = "Could not find executable: " + result.Executable;
            }

            result.OriginKind = PackageOrigin.FromEntries(hasEmbeddedProvision, hasScInfo);
            result.LaunchImageJson = LaunchEvidenceCodec.Serialize(launchSizes, launchPrefixes.Storyboard, iconSides);
            if (result.IconCandidates.Count == 0) result.Warning ??= "No usable icon candidates";
            return result;
        }
        catch (InvalidDataException ex)
        {
            result.Error = "Not a valid zip: " + ex.Message;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.GetType().Name + ": " + ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 解码选中的图标：优先自写 <see cref="PngCodec"/>（含 CgBI 还原）；
    /// **解不了就交给 <paramref name="fallbackDecoder"/>**（WPF/WIC）——它同时覆盖
    /// JPEG 图标、调色板/隔行/16 位等自写解码器不支持的 PNG（实测这类包不罕见）。
    /// 然后缩到档位并重新编码成标准 PNG。返回 <c>null</c> 表示失败（**不算解析失败**）。
    /// </summary>
    public static IconLoadResult? LoadIcon(string path, IpaIconEntry candidate, int bucketPx,
        Func<byte[], PngCodec.DecodedPng?>? fallbackDecoder, out string? error)
    {
        error = null;
        try
        {
            byte[] raw;
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName == candidate.EntryName);
                if (entry is null)
                {
                    error = "Icon entry not found: " + candidate.EntryName;
                    return null;
                }

                raw = ReadEntry(entry);
            }

            var iconKey = Convert.ToHexString(SHA1.HashData(raw)).ToLowerInvariant();

            PngCodec.DecodedPng decoded;
            var usedFallback = false;
            if (PngCodec.IsPng(raw))
            {
                try
                {
                    decoded = PngCodec.Decode(raw);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
                {
                    // 调色板 / 隔行 / 16 位 / 异常 chunk：自写解码器不支持 → 交给 WIC
                    decoded = fallbackDecoder?.Invoke(raw)
                              ?? throw new NotSupportedException("PNG variant requires WIC: " + ex.Message);
                    usedFallback = true;
                }
            }
            else if (candidate.IsJpeg)
            {
                decoded = fallbackDecoder?.Invoke(raw)
                          ?? throw new NotSupportedException("JPEG icon requires the WPF decoder");
                usedFallback = true;
            }
            else
            {
                throw new InvalidDataException("Unknown icon format");
            }

            var scaled = PngCodec.Downscale(decoded, bucketPx);
            var png = PngCodec.Encode(scaled);
            return new IconLoadResult(png, iconKey, scaled.Width, scaled.Height, usedFallback);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    private static int CountSlashes(string value)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c == '/') count++;
        }

        return count;
    }

    private static bool IsImage(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("iTunesArtwork", StringComparison.OrdinalIgnoreCase);

    /// <summary>判定候选来源；返回 null 表示不是图标候选。</summary>
    private static (IconSource? Source, bool IsArtwork) ClassifyIcon(string name, string appPrefix,
        HashSet<string> plistNames)
    {
        // ① IPA 根目录的 iTunesArtwork（可能在根，也可能没有扩展名）
        if (!name.Contains('/') && name.StartsWith("iTunesArtwork", StringComparison.OrdinalIgnoreCase))
            return (IconSource.StoreArtwork, true);

        // ② .app 根目录（不能再深一层）
        if (!name.StartsWith(appPrefix, StringComparison.OrdinalIgnoreCase)) return (null, false);
        var relative = name[appPrefix.Length..];
        if (relative.Contains('/')) return (null, false);

        var baseName = Path.GetFileNameWithoutExtension(relative);
        if (plistNames.Contains(baseName) || plistNames.Contains(relative))
            return (IconSource.PlistNamed, false);

        return relative.Contains("icon", StringComparison.OrdinalIgnoreCase)
            ? (IconSource.RootIconName, false)
            : (null, false);
    }

    /// <summary>Info.plist 里点名的图标（基础名，去扩展名）。</summary>
    private static HashSet<string> PlistIconNames(Dictionary<string, object?>? dict)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (dict is null) return names;

        var iconName = Str(dict, "CFBundleIconName");
        if (iconName.Length > 0) names.Add(Path.GetFileNameWithoutExtension(iconName));

        foreach (var key in new[] { "CFBundleIcons", "CFBundleIcons~ipad" })
        {
            if (!dict.TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> icons) continue;
            if (icons.TryGetValue("CFBundlePrimaryIcon", out var primary) && primary is Dictionary<string, object?> primaryDict)
                AddArray(primaryDict, "CFBundleIconFiles");
        }

        AddArray(dict, "CFBundleIconFiles");
        AddArray(dict, "CFBundleIconFiles~ipad");
        return names;

        void AddArray(Dictionary<string, object?> source, string key)
        {
            if (!source.TryGetValue(key, out var raw) || raw is not List<object?> list) return;
            foreach (var item in list)
            {
                if (item is not string s || s.Length == 0) continue;
                names.Add(Path.GetFileNameWithoutExtension(s));
                names.Add(s);
            }
        }
    }

    private static string Str(Dictionary<string, object?>? dict, string key) =>
        dict is not null && dict.TryGetValue(key, out var value) && value is string s ? s : "";

    private sealed record LaunchPrefixSet(List<string> Prefixes, bool Storyboard);

    private static LaunchPrefixSet LaunchPrefixes(Dictionary<string, object?>? dict)
    {
        var prefixes = new List<string> { "Default", "LaunchImage" };
        foreach (var key in new[] { "UILaunchImageFile", "UILaunchImageFile~ipad" })
        {
            var raw = Str(dict, key);
            if (raw.Length == 0) continue;
            var name = Path.GetFileName(raw);
            if (name.Length > 0) prefixes.Add(name);
        }

        var storyboard = Str(dict, "UILaunchStoryboardName").Length > 0;
        return new LaunchPrefixSet(prefixes, storyboard);
    }

    /// <summary>UILaunchImages[].UILaunchImageSize（字符串 "{375, 667}" 或数组 [375,667]）。</summary>
    private static void AddPlistLaunchSizes(Dictionary<string, object?>? dict, List<(int Width, int Height)> sizes)
    {
        if (dict is null || !dict.TryGetValue("UILaunchImages", out var raw) || raw is not List<object?> list) return;
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> image) continue;
            if (!image.TryGetValue("UILaunchImageSize", out var size)) continue;
            if (TryParseSize(size, out var w, out var h)) sizes.Add((w, h));
        }
    }

    private static bool TryParseSize(object? value, out int width, out int height)
    {
        width = height = 0;
        if (value is List<object?> list && list.Count >= 2)
        {
            if (list[0] is long a && list[1] is long b) { width = (int)a; height = (int)b; return true; }
            if (int.TryParse(list[0]?.ToString(), out var x) && int.TryParse(list[1]?.ToString(), out var y))
            { width = x; height = y; return true; }
            return false;
        }

        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var digits = text.Split(new[] { '{', '}', ',', ' ', ';', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
        return digits.Length >= 2 && int.TryParse(digits[0], out width) && int.TryParse(digits[1], out height);
    }

    /// <summary>启动图名：Default/LaunchImage/自定义前缀 + 可选 -/@/扩展名；排除 default_texture 这类资源。</summary>
    private static bool IsLaunchName(string fullName, string appPrefix, LaunchPrefixSet prefixes)
    {
        if (!fullName.StartsWith(appPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var baseName = Path.GetFileName(fullName);
        foreach (var prefix in prefixes.Prefixes)
        {
            if (!baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = baseName[prefix.Length..];
            if (rest.Length == 0 || rest[0] is '-' or '@' or '.') return true;
        }

        return false;
    }

    private static string FamilyText(Dictionary<string, object?>? dict)
    {
        if (dict is null || !dict.TryGetValue("UIDeviceFamily", out var raw) || raw is not List<object?> list) return "";
        var phone = list.Any(v => v is long l && l == 1);
        var pad = list.Any(v => v is long l && l == 2);
        return (phone, pad) switch
        {
            (true, true) => "Universal",
            (true, false) => "iPhone",
            (false, true) => "iPad",
            _ => "",
        };
    }

    private static string CapsJson(Dictionary<string, object?>? dict)
    {
        if (dict is null || !dict.TryGetValue("UIRequiredDeviceCapabilities", out var raw) || raw is null) return "";
        try { return JsonSerializer.Serialize(raw); } catch { return ""; }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>读取条目头部（最多 <paramref name="count"/> 字节；图标头与 Mach-O 头都够用）。</summary>
    private static byte[] ReadHead(ZipArchiveEntry entry, int count)
    {
        using var stream = entry.Open();
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }

        if (read == count) return buffer;
        var trimmed = new byte[read];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, read);
        return trimmed;
    }
}

/// <summary>读图片头：PNG（按 chunk 走，CgBI 也能过）/ JPEG（SOF 段）。</summary>
internal static class ImageProbe
{
    public static (int Width, int Height, bool IsCgbi, bool IsJpeg) Probe(byte[] head)
    {
        if (head.Length >= 24 && head[0] == 0x89 && head[1] == 0x50)
        {
            var isCgbi = head.Length >= 16 && Encoding.ASCII.GetString(head, 12, 4) == "CgBI";
            var offset = 8;
            while (offset + 16 <= head.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(offset, 4));
                var type = Encoding.ASCII.GetString(head, offset + 4, 4);
                if (type == "IHDR")
                {
                    var width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(offset + 8, 4));
                    var height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(offset + 12, 4));
                    return (width, height, isCgbi, false);
                }

                if (length < 0) break;
                offset += 12 + length;
            }

            return (0, 0, isCgbi, false);
        }

        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xD8)
        {
            var pos = 2;
            while (pos + 4 <= head.Length)
            {
                if (head[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }

                var marker = head[pos + 1];
                var length = (head[pos + 2] << 8) | head[pos + 3];
                if (marker is 0xC0 or 0xC1 or 0xC2 && pos + 9 <= head.Length)
                    return ((head[pos + 7] << 8) | head[pos + 8], (head[pos + 5] << 8) | head[pos + 6], false, true);
                if (length < 2) break;
                pos += 2 + length;
            }

            return (0, 0, false, true);
        }

        return (0, 0, false, false);
    }
}

/// <summary>Mach-O 头解析（fat / fat64 / thin / 反转魔数）。</summary>
internal static class MachO
{
    public static string ReadArch(byte[] head)
    {
        if (head.Length < 8) return "";
        var magic = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4));
        var archs = new List<string>();

        switch (magic)
        {
            case 0xCAFEBABE:
            case 0xCAFEBABF:
            {
                var is64 = magic == 0xCAFEBABF;
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4));
                var offset = 8;
                var stride = is64 ? 32 : 20;
                for (var i = 0; i < count && offset + 8 <= head.Length; i++, offset += stride)
                {
                    var cpuType = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(offset, 4));
                    var subType = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(offset + 4, 4)) & 0x00FFFFFF;
                    Add(archs, cpuType, subType);
                }

                break;
            }

            case 0xBEBAFECA:
            case 0xBFBAFECA:
            {
                var is64 = magic == 0xBFBAFECA;
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4));
                var offset = 8;
                for (var i = 0; i < count && offset + 8 <= head.Length; i++, offset += is64 ? 32 : 20)
                {
                    var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(offset, 4));
                    var subType = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(offset + 4, 4)) & 0x00FFFFFF;
                    Add(archs, cpuType, subType);
                }

                break;
            }

            case 0xFEEDFACE:
            case 0xFEEDFACF:
                Add(archs, BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4)),
                    BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8, 4)) & 0x00FFFFFF);
                break;

            case 0xCEFAEDFE:
            case 0xCFFAEDFE:
                Add(archs, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8, 4)) & 0x00FFFFFF);
                break;

            default:
                return "unknown(0x" + magic.ToString("X8") + ")";
        }

        return archs.Count == 0 ? "" : string.Join(",", archs.Distinct());
    }

    private static void Add(List<string> target, uint cpuType, uint subType)
    {
        if (cpuType == 12)
            target.Add(subType switch { 6 => "armv6", 9 => "armv7", 11 => "armv7s", _ => "arm-" + subType });
        else if (cpuType == 0x0100000C)
            target.Add(subType == 2 ? "arm64e" : "arm64");
        else
            target.Add("cpu-" + cpuType);
    }
}
