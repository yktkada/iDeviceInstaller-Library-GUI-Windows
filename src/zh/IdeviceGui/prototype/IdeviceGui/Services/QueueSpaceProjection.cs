namespace IdeviceGui.Services;

/// <summary>
/// 回读完成后的队列空间预演（2026-11-07 用户定）：
/// **只提示、不改队列**——按引擎执行顺序逐个模拟安装任务，返回当前空间下装不下的任务。
/// <para>
/// 口径：峰值 = 压缩包 + 解压后；解压体积未知时按压缩包 × 2（用户定）。
/// 装不下的任务不扣减可用空间（模拟它不会执行），继续评估后面的任务。
/// 卸载任务不占空间，直接跳过；运行中的任务不参与（它已经开始）。
/// </para>
/// </summary>
internal sealed record QueueSpaceProjection(
    long FreeBytes,
    IReadOnlyList<QueueItem> CannotFit);

internal static class QueueSpaceProjectionCalculator
{
    /// <summary>与 QueueService 的单任务空间检查同一余量。</summary>
    public const long MarginBytes = 10L * 1024 * 1024;

    public static QueueSpaceProjection Check(long freeBytes, IReadOnlyList<QueueItem> snapshot)
    {
        var canNotFit = new List<QueueItem>();
        var free = freeBytes;

        foreach (var item in snapshot)
        {
            if (item.Kind != JobKind.Install) continue;
            if (item.Status.IsFinished() || item.Status.IsInFlight()) continue;

            var peak = PeakBytes(item);
            if (peak <= 0)
            {
                // 体积未知到连压缩包都读不到：无法证明放得下，按装不下提示。
                canNotFit.Add(item);
                continue;
            }

            var required = peak + MarginBytes;
            if (free < required)
            {
                canNotFit.Add(item);
                continue;   // 不执行，不扣 free
            }

            free -= peak;
        }

        return new QueueSpaceProjection(freeBytes, canNotFit);
    }

    /// <summary>
    /// 峰值统一由入队时算进 <see cref="QueueItem.PeakBytes"/>（解压未知按压缩 ×2）。
    /// 投影在 UI 线程只读内存，不再做 FileInfo 同步 IO。
    /// </summary>
    private static long PeakBytes(QueueItem item) => item.PeakBytes > 0 ? item.PeakBytes : 0;
}
