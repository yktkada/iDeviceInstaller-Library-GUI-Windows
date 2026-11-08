using IdeviceGui.Services;

namespace IdeviceGui.ViewModels.Library;

/// <summary>资料库行标记色（2026-11-01 用户定：黄 &gt; 红 &gt; 无；2026-11-02 加绿色 = 队列安装成功）。</summary>
internal enum LibraryMarker
{
    /// <summary>普通文件（白/默认底色）。</summary>
    None = 0,

    /// <summary>该包**在当前选中设备上安装成功**（绿；临时提示，只存内存，刷新即清，见 docs/06 §6.14.6）。</summary>
    Installed = 1,

    /// <summary>在这台设备上安装失败（红）。</summary>
    Failed = 2,

    /// <summary>在当前设备队列里（黄；含待装/上传/安装/暂停/待确认/装完待卸载）。</summary>
    Queued = 3,
}

internal static class LibraryMarkers
{
    /// <summary>队列状态 → 是否算“在队列里（黄）”/“失败（红）”。Done/Cancelled 不参与染色。</summary>
    public static (bool Queued, bool Failed) Classify(QueueStatus status)
        => status == QueueStatus.Failed
            ? (false, true)
            : status.IsQueuedMarker()
                ? (true, false)
                : (false, false);   // Done / Cancelled（IsFinished 已穷举，未知状态会抛，不会静默）

    /// <summary>优先级（2026-11-02 用户定）：黄 &gt; 红 &gt; 绿 &gt; 无。</summary>
    public static LibraryMarker Resolve(bool queued, bool failed, bool installed)
        => queued ? LibraryMarker.Queued
            : failed ? LibraryMarker.Failed
            : installed ? LibraryMarker.Installed
            : LibraryMarker.None;

    /// <summary>
    /// 组行状态 = **成员聚合**（2026-11-02 用户定：集合里任一成员有颜色 → 组行同色；
    /// 绿画在组行图标列、勾画在 ✓ 列）。复用普通行的 <see cref="Resolve"/>，优先级完全一致，不另立规则。
    /// </summary>
    public static LibraryMarker ResolveGroup(IEnumerable<LibraryMarker> members)
    {
        var queued = false;
        var failed = false;
        var installed = false;
        foreach (var marker in members)
        {
            switch (marker)
            {
                case LibraryMarker.Queued: queued = true; break;
                case LibraryMarker.Failed: failed = true; break;
                case LibraryMarker.Installed: installed = true; break;
            }
        }

        return Resolve(queued, failed, installed);
    }
}
