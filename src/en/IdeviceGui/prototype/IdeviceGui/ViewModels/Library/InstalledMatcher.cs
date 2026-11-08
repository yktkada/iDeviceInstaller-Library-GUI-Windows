using IdeviceGui.Services;

namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// 资料库行「✓ = 设备上装着这个包」的判定（2026-11-02 用户定）：
/// **必须 BundleId + 版本都精确匹配**，不得只比 BundleId；任一侧版本未知 → **不算已装**（宁可少勾，不可假勾）。
/// <para>版本口径与回读一致：`CFBundleShortVersionString` 优先、缺失回退 `CFBundleVersion`（见 AGENTS §5.9）。</para>
/// </summary>
internal enum InstalledMatchKind
{
    None,
    SameVersion,
    OtherVersion,
}

internal static class InstalledMatcher
{
    /// <summary>
    /// 设备回读匹配（2026-11-06 用户定）：名称 + BundleId + 版本；名称区分大小写，大小不参与。
    /// </summary>
    public static bool Matches(IReadOnlyList<InstalledApp>? apps, string? bundleId, string? packageVersion, string? name)
    {
        var installed = InstalledAppSet.Find(apps, bundleId ?? "");
        if (installed is null) return false;
        if (!NameMatches(installed.Value, name)) return false;

        // 复用 QueueService 的版本判定：任一侧未知返回 null → 视为"不匹配"（无法证明同版本）
        return InstalledAppSet.VersionsMatch(installed.Value, packageVersion) == true;
    }

    /// <summary>
    /// 三态：无同 BundleId（或名称不同）/ 同 BundleId 同版本 / 同 BundleId 其他版本。
    /// </summary>
    public static InstalledMatchKind Classify(IReadOnlyList<InstalledApp>? apps, string? bundleId, string? packageVersion, string? name)
    {
        var installed = InstalledAppSet.Find(apps, bundleId ?? "");
        if (installed is null || !NameMatches(installed.Value, name)) return InstalledMatchKind.None;
        return InstalledAppSet.VersionsMatch(installed.Value, packageVersion) == true
            ? InstalledMatchKind.SameVersion
            : InstalledMatchKind.OtherVersion;
    }

    /// <summary>名称区分大小写；设备回读名称与资料库名称都做 Trim。</summary>
    public static bool NameMatches(InstalledApp installed, string? name)
        => string.Equals(installed.DisplayName.Trim(), (name ?? "").Trim(), StringComparison.Ordinal);

    // 兼容旧调用（测试/工具仍可能用三参数）：名称不参与，保持旧语义。
    public static bool Matches(IReadOnlyList<InstalledApp>? apps, string? bundleId, string? packageVersion)
    {
        var installed = InstalledAppSet.Find(apps, bundleId ?? "");
        return installed is not null && InstalledAppSet.VersionsMatch(installed.Value, packageVersion) == true;
    }

    public static InstalledMatchKind Classify(IReadOnlyList<InstalledApp>? apps, string? bundleId, string? packageVersion)
    {
        var installed = InstalledAppSet.Find(apps, bundleId ?? "");
        if (installed is null) return InstalledMatchKind.None;
        return InstalledAppSet.VersionsMatch(installed.Value, packageVersion) == true
            ? InstalledMatchKind.SameVersion
            : InstalledMatchKind.OtherVersion;
    }
}
