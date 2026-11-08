namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// 双击判定（2026-11-01 用户定：统一 400 ms，**不用** WPF MouseDoubleClick——它跟系统双击时间，默认 500 ms 且用户可改）。
/// 纯函数，便于单测：同一次按下与上一次按下的间隔 ≤ 400 ms 视为一次双击。
/// </summary>
internal static class DoubleClickState
{
    public const int WindowMs = 400;

    /// <summary>上一次按下（同毫秒时钟）到本次按下的间隔是否在双击窗口内。</summary>
    public static bool IsSecondClick(long? lastClickMs, long nowMs)
        => lastClickMs is { } last && nowMs - last >= 0 && nowMs - last <= WindowMs;
}
