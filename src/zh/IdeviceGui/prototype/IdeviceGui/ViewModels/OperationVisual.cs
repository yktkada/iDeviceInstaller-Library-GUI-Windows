using System.Windows.Media;
using IdeviceGui.Services;

namespace IdeviceGui.ViewModels;

/// <summary>
/// 统一操作颜色状态（2026-11-06 用户定）：队列 / 资料库 / 设备应用 / 设备 Tab 共用。
/// 颜色只由 <see cref="OperationVisuals.Resolve(JobKind, QueueStatus)"/> 决定，界面不再各写一套。
/// </summary>
internal enum OperationVisual
{
    None = 0,
    InstallWaiting = 1,
    InstallActive = 2,
    UninstallWaiting = 3,
    UninstallActive = 4,
    Paused = 5,
    Succeeded = 6,
    Failed = 7,
}

/// <summary>操作状态 → 颜色 / 优先级。所有区域必须走这里。</summary>
internal static class OperationVisuals
{
    // 2026-11-06 用户确认的色板。
    public static readonly SolidColorBrush InstallWaitingBrush = Frozen("#FFB4D2F0");
    public static readonly SolidColorBrush InstallActiveBrush = Frozen("#FF7FA9DC");
    public static readonly SolidColorBrush UninstallWaitingBrush = Frozen("#FFFFDD85");
    public static readonly SolidColorBrush UninstallActiveBrush = Frozen("#FFEDB93F");
    public static readonly SolidColorBrush PausedBrush = Frozen("#FFD0D0D0");
    public static readonly SolidColorBrush SucceededBrush = Frozen("#FFD9F2D9");
    public static readonly SolidColorBrush FailedBrush = Frozen("#FFFFD9D9");
    public static readonly SolidColorBrush NoOperationBrush = Frozen("#FFFFFFFF");

    public static OperationVisual Resolve(JobKind kind, QueueStatus status)
    {
        if (status == QueueStatus.Failed) return OperationVisual.Failed;
        if (status == QueueStatus.Done) return OperationVisual.Succeeded;
        if (status is QueueStatus.Paused or QueueStatus.NeedsConfirm or QueueStatus.Suspended) return OperationVisual.Paused;
        if (status == QueueStatus.Cancelled) return OperationVisual.None;

        var active = status is QueueStatus.Installing or QueueStatus.Uninstalling or QueueStatus.Readback;
        if (kind == JobKind.Uninstall)
            return active ? OperationVisual.UninstallActive : OperationVisual.UninstallWaiting;

        return active ? OperationVisual.InstallActive : OperationVisual.InstallWaiting;
    }

    public static SolidColorBrush ToBrush(this OperationVisual visual) => visual switch
    {
        OperationVisual.InstallWaiting => InstallWaitingBrush,
        OperationVisual.InstallActive => InstallActiveBrush,
        OperationVisual.UninstallWaiting => UninstallWaitingBrush,
        OperationVisual.UninstallActive => UninstallActiveBrush,
        OperationVisual.Paused => PausedBrush,
        OperationVisual.Succeeded => SucceededBrush,
        OperationVisual.Failed => FailedBrush,
        _ => NoOperationBrush,
    };

    /// <summary>聚合优先级：失败 &gt; 卸载中 &gt; 安装中 &gt; 暂停 &gt; 卸载等待 &gt; 安装等待 &gt; 成功 &gt; 无。</summary>
    public static int Priority(OperationVisual visual) => visual switch
    {
        OperationVisual.Failed => 7,
        OperationVisual.UninstallActive => 6,
        OperationVisual.InstallActive => 5,
        OperationVisual.Paused => 4,
        OperationVisual.UninstallWaiting => 3,
        OperationVisual.InstallWaiting => 2,
        OperationVisual.Succeeded => 1,
        _ => 0,
    };

    public static OperationVisual Max(IEnumerable<OperationVisual> visuals)
    {
        var best = OperationVisual.None;
        foreach (var visual in visuals)
            if (Priority(visual) > Priority(best))
                best = visual;
        return best;
    }

    public static bool IsActive(OperationVisual visual)
        => visual is OperationVisual.InstallActive or OperationVisual.UninstallActive;

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
