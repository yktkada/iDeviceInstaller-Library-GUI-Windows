namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// ✓ 列状态（2026-11-01）：未装 / 已装。
/// 2026-11-02 用户定：**`◐`（部分已装）废弃**——组行"任一成员已装 → 打勾"，不再区分部分。
/// 2026-11-05 用户定：新增 `OtherVersion`——同 BundleId 但版本不同/未知，勾列浅蓝底、不显示勾。
/// </summary>
internal enum LibraryInstalledState
{
    None = 0,
    Yes = 1,
    /// <summary>同 BundleId 已安装，但版本不同或版本未知（2026-11-05）。</summary>
    OtherVersion = 2,
}

/// <summary>
/// ✓ 列状态的**唯一聚合口径**（2026-11-02：组行复用普通行接口，不再各写一套）。
/// 普通行传 `(0|1, 1)`，组行传「组内已装数 / 成员数」；**只要有一个已装就打勾**。
/// </summary>
internal static class LibraryInstalledStates
{
    /// <summary>已装数 &gt; 0 且成员数 &gt; 0 → 打勾；否则空白。</summary>
    public static LibraryInstalledState Resolve(int sameVersion, int otherVersion, int total)
    {
        if (total <= 0) return LibraryInstalledState.None;
        if (sameVersion > 0) return LibraryInstalledState.Yes;
        if (otherVersion > 0) return LibraryInstalledState.OtherVersion;
        return LibraryInstalledState.None;
    }

    /// <summary>旧两参重载（同版本数；无其他版本）。</summary>
    public static LibraryInstalledState Resolve(int installed, int total)
        => Resolve(installed, 0, total);
}
