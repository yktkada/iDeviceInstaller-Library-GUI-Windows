namespace IdeviceGui.Services;

/// <summary>
/// 可比较的 iOS 版本（"4.3" / "8.1.2" / "8.4.1"）。缺失段按 0 处理（"7.1" == "7.1.0"）。
/// </summary>
internal readonly record struct IosVersion(int Major, int Minor, int Patch) : IComparable<IosVersion>
{
    public static bool TryParse(string? text, out IosVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        var parts = trimmed.Split('.');
        if (parts.Length is < 1 or > 3) return false;   // 复审 B5：只接受 1–3 段，不静默丢段

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0)
                return false;
            numbers[i] = n;
        }

        version = new IosVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(IosVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;
        var byMinor = Minor.CompareTo(other.Minor);
        return byMinor != 0 ? byMinor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// 版本前置校验（2026-10-31 用户要求）：**入队与任务开始前**先比对 IPA 的 `MinimumOSVersion` 与设备 iOS，
/// 不满足就直接拒绝/判失败——不要等几十 MB 传完、装到一半才在设备上报"版本太低"。
/// <para>
/// 这是 M5（完整兼容性判定：架构 / 设备族 / RequiredCapture）之前的**最小一道闸**，不替代 M5。
/// </para>
/// </summary>
internal static class VersionRules
{
    /// <summary>
    /// 返回 null = 能装或无法判断（包没有 MinOs、设备版本读不到 → 不拦，交给设备自己判）；
    /// 返回非空 = 需要拦截的原因文本（给用户看，会进日志与任务备注）。
    /// </summary>
    public static string? CheckMinimumOs(string? packageMinOs, string? deviceVersion)
    {
        if (!IosVersion.TryParse(packageMinOs, out var required)) return null;
        if (!IosVersion.TryParse(deviceVersion, out var device)) return null;

        return required.CompareTo(device) > 0
            ? $"需要 iOS {packageMinOs!.Trim()} 或更新版本。设备已安装 iOS {deviceVersion!.Trim()}"
            : null;
    }
}
