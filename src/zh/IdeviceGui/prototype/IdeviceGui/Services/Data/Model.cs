namespace IdeviceGui.Services.Data;

/// <summary>
/// 身份种类。<b>同一行只填一套身份</b>：<see cref="FileId"/> 行只写三列文件 ID，
/// <see cref="PathFallback"/> 行只写 <see cref="PackageRow.NormalizedPath"/>；翻转身份按"删除 + 新增"
/// （docs/09 §3.1「身份切换规则」，2026-10-29 用户拍板）。
/// </summary>
internal enum IdentityKind
{
    FileId,
    PathFallback,
}

/// <summary>解析状态。列值就是这里的名字（docs/09 §3.1 <c>ParseState</c>）。</summary>
internal static class ParseStates
{
    public const string Pending = "Pending";
    public const string Ok = "Ok";
    public const string Failed = "Failed";
}

/// <summary>
/// <c>Packages</c> 一行。B7 只填扫描 / 解析能得到的那部分列；
/// <c>Sdk</c> / <c>DeviceFamily</c> / <c>RequiredCaps</c> / <c>Arch</c> / 体积列 / <c>IconKey</c> 留给 M3。
/// </summary>
internal sealed class PackageRow
{
    public long Id { get; set; }
    public long ScanRootId { get; set; }

    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? NormalizedPath { get; set; }
    public IdentityKind IdentityKind { get; set; } = IdentityKind.FileId;
    public ulong? VolumeSerial { get; set; }
    public ulong? FileIdHigh { get; set; }
    public ulong? FileIdLow { get; set; }

    public long FileSize { get; set; }
    public string FileMtimeUtc { get; set; } = "";
    public string? ContentHash { get; set; }

    public string? DisplayName { get; set; }
    public string? BundleId { get; set; }
    public string? Version { get; set; }
    public string? Build { get; set; }
    public string? MinOs { get; set; }
    public string? AppDirName { get; set; }

    // ── M3 深解析（docs/04 §3.3–§3.4）──
    public string? Sdk { get; set; }
    public string? DeviceFamily { get; set; }
    public string? RequiredCaps { get; set; }
    public string? Arch { get; set; }
    public long CompressedSize { get; set; }
    public long UncompressedSize { get; set; }
    public long PayloadSize { get; set; }
    public long AppSize { get; set; }
    public long ExecutableSize { get; set; }
    public long FrameworksSize { get; set; }
    public long PlugInsSize { get; set; }
    public string? IconKey { get; set; }

    /// <summary>M5-lite 适配证据（启动图/图标尺寸 JSON；见 LaunchEvidenceCodec）。</summary>
    public string? LaunchImageJson { get; set; }

    /// <summary>IPA 来源（PackageOrigin 常量；2026-11-05）。</summary>
    public string? OriginKind { get; set; }

    public int ParseVersion { get; set; }
    public string ParseState { get; set; } = ParseStates.Pending;
    public string? ParseError { get; set; }

    public bool ExistsFlag { get; set; } = true;

    public string? FirstSeenUtc { get; set; }
    public string? LastSeenUtc { get; set; }
    public string? LastInstallUtc { get; set; }
    public string? LastInstallUdid { get; set; }
}

/// <summary><c>ScanRoots</c> 一行。</summary>
internal sealed class ScanRootRow
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? AddedUtc { get; set; }
}

/// <summary><c>Devices</c> 一行（M2 只写刷新设备时拿得到的字段）。</summary>
internal sealed class DeviceRow
{
    public string Udid { get; set; } = "";
    public string? Name { get; set; }
    public string? ProductType { get; set; }
    public string? IosVersion { get; set; }
    public string? CpuArch { get; set; }
    public long? TotalBytes { get; set; }
    public long? FreeBytes { get; set; }
    public string? LastSeenUtc { get; set; }
}

/// <summary>时间列统一格式：UTC、固定宽度、可直接按字符串比较（docs/09 §2）。</summary>
internal static class UtcText
{
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public static string Now() => FormatUtc(DateTime.UtcNow);

    public static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString(Format,
        System.Globalization.CultureInfo.InvariantCulture);

    public static DateTime? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParseExact(text, Format, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var dt)
            ? dt
            : null;
    }
}
