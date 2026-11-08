namespace IdeviceGui.Services;

using IdeviceGui.Services.Diagnostics;

// ─────────────────────────────────────────────────────────────────────────────
// 契约层：队列状态机、进程封装、AFC helper、回读 之间只通过这些类型交互。
// 目的：B1+B2 并行开发时各实现方只依赖本文件，互不改对方的文件。
// 定义来源：docs/07 §3.1（状态代号）、§3.3（超时）、§3.4（空间）、
//           docs/10 §3.1（AfcHelper 协议）、docs/02 §3（设备字段）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>队列任务种类。与 07 §3.1 的 Kind 列一致。</summary>
internal enum JobKind
{
    Install,
    Uninstall,
}

/// <summary>
/// 队列状态。代号与 07 §3.1 完全一致（将来直接写 DB 的 Status 列），
/// 界面中文文案由 <see cref="QueueStatusText"/> 单独映射，不在枚举里混文案。
/// </summary>
internal enum QueueStatus
{
    Pending,
    VerifyDevice,
    Precheck,
    Cleaning,
    SpaceCheck,
    Installing,
    Uninstalling,
    Readback,
    Paused,
    Failed,

    /// <summary>用户取消（2026-11-01）：未开始 / 上传中断 → Cancelled，不算Failed；Installing的取消走「装完自动卸载」。</summary>
    Cancelled,

    /// <summary>
    /// 回读结果Unknown（命令异常/超时，且设备仍在线）。
    /// **2026-11-07 起已不可达**：回读异常 3 次后直接 Failed；枚举保留以稳定状态编号与旧数据兼容。
    /// </summary>
    NeedsConfirm,

    /// <summary>队列级软挂起（2026-11-07）：任务尚未开始，继续时直接回 Pending，不触发中断恢复回读。</summary>
    Suspended,

    /// <summary>仅内存态：本次会话内保留的"Completed"显示，Completed不写库（07 §3.1）。</summary>
    Done,
}

/// <summary>队列状态的中文文案映射。界面只绑定这里的结果。</summary>
internal static class QueueStatusText
{
    public static string ToText(this QueueStatus status) => status switch
    {
        QueueStatus.Pending => "Waiting",
        QueueStatus.VerifyDevice => "Verifying device",
        QueueStatus.Precheck => "Checking",
        QueueStatus.Cleaning => "Cleaning",
        QueueStatus.SpaceCheck => "Checking space",
        QueueStatus.Installing => "Installing",
        QueueStatus.Uninstalling => "Uninstalling",
        QueueStatus.Readback => "Reading back",
        QueueStatus.Paused => "Paused",
        QueueStatus.Failed => "Failed",
        QueueStatus.NeedsConfirm => "Needs confirmation",
        QueueStatus.Suspended => "Suspended",
        QueueStatus.Done => "Completed",
        QueueStatus.Cancelled => "Cancelled",
        _ => "Unknown",
    };
}

/// <summary>一台设备队列（泵）的整体运行态。与单个任务的 <see cref="QueueStatus"/> 不同层。</summary>
internal enum QueueRunState
{
    Idle,
    Running,
    Paused,
    Suspended,
    Offline,
}

internal static class QueueRunStateText
{
    public static string ToText(this QueueRunState state) => state switch
    {
        QueueRunState.Idle => "Idle",
        QueueRunState.Running => "Running",
        QueueRunState.Paused => "Paused",
        QueueRunState.Suspended => "Suspended",
        QueueRunState.Offline => "Offline",
        _ => "Unknown",
    };
}

/// <summary>一次设备探测的结果。字段名对齐 ideviceinfo 的键（总览 §6.9）。</summary>
internal readonly record struct DeviceProbe(
    bool Present,
    string Name,
    string ProductType,
    string Version,
    string CpuArch,
    string Build = "",
    string Color = "",
    string Ecid = "",
    string Serial = "",
    string ModelNumber = "",
    string HardwareModel = "")
{
    public static DeviceProbe Missing => new(false, "", "", "", "");
}

/// <summary>磁盘容量。<see cref="Ok"/> 为 false 时两个字节数无意义。</summary>
internal readonly record struct DiskUsage(bool Ok, long TotalBytes, long FreeBytes)
{
    public static DiskUsage Unknown => new(false, 0, 0);
}

/// <summary>
/// ideviceinstaller install 的结果。超时被杀是独立情况：
/// 杀完必须再探测设备——离线则暂停队列，在线则记Failed继续（07 §3.3）。
/// </summary>
internal readonly record struct InstallOutcome(
    int ExitCode,
    bool HasErrorLine,
    string? FirstError,
    bool TimedOut)
{
    /// <summary>B1 阶段为 2/3 条判定（退出码 + 无 ERROR 行）；回读在 B3 接入。</summary>
    public bool LooksSuccessful => !TimedOut && ExitCode == 0 && !HasErrorLine;
}

internal readonly record struct UninstallOutcome(
    int ExitCode,
    bool HasErrorLine,
    string? FirstError,
    bool TimedOut)
{
    /// <summary>
    /// 注意：这只是"CLI 没报错"，不是最终成功判定。iOS 6/7 卸载不存在的 App 会返回 0（假成功），
    /// 真正的判定要靠回读（07 §3.6、总览 §6.3）；B3 之前该缺口明确记录在进度文档里。
    /// </summary>
    public bool LooksSuccessful => !TimedOut && ExitCode == 0 && !HasErrorLine;
}

/// <summary>
/// AFC helper（AfcHelper.exe）执行清理的结果。
/// TimedOut 与 DeviceUnreachable 都会导致暂停该设备队列（07 §3.3）。
/// </summary>
internal readonly record struct AfcCleanOutcome(
    int Removed,
    bool DeviceUnreachable,
    bool TimedOut,
    string? Warning);

internal readonly record struct AfcSpaceOutcome(
    bool DeviceUnreachable,
    bool TimedOut,
    long TotalBytes,
    long FreeBytes,
    string? Error)
{
    public bool Ok => !DeviceUnreachable && !TimedOut && Error is null;
}

/// <summary>设备上已安装的一个 App。版本"Unknown"是合法结果（总览 §6.10）。</summary>
internal readonly record struct InstalledApp(string BundleId, string Version, bool VersionKnown, string DisplayName = "");

/// <summary>
/// CLI 子进程封装。所有方法异步、可取消，内部负责"无输出判卡死 + 超时杀进程树"。
/// 实现：CliRunner（Services/CliRunner 的实现方负责）。
/// </summary>
internal interface ICliRunner
{
    /// <summary>idevice_id -l；超时 5 s，超时视为离线（返回空列表）。</summary>
    Task<IReadOnlyList<string>> ListUdidsAsync(CancellationToken ct);

    /// <summary>ideviceinfo 探测；超时 15 s，超时视为设备无响应。</summary>
    Task<DeviceProbe> ProbeAsync(string udid, CancellationToken ct);

    /// <summary>安装。超时判据是"180 s 无新输出"，不是总时长（07 §3.3）。</summary>
    Task<InstallOutcome> InstallAsync(
        string udid,
        string ipaPath,
        IProgress<string>? log,
        IProgress<double>? percent,
        CancellationToken ct);

    /// <summary>卸载。退出码只作参考（iOS 6/7 假成功），B3 起由回读兜底。</summary>
    Task<UninstallOutcome> UninstallAsync(
        string udid,
        string bundleId,
        IProgress<string>? log,
        CancellationToken ct);

    /// <summary>ideviceinfo -q com.apple.disk_usage 取 TotalDataCapacity / TotalDataAvailable。</summary>
    Task<DiskUsage> QueryDiskUsageAsync(string udid, CancellationToken ct);

    /// <summary>ideviceinfo -q com.apple.mobile.battery 取实时电量 / 充电状态（99–251 ms）。</summary>
    Task<BatteryUsage> ReadBatteryUsageAsync(string udid, CancellationToken ct);

    /// <summary>idevicediagnostics diagnostics GasGauge 取电池健康（161–309 ms）。</summary>
    Task<BatteryHealth> ReadBatteryHealthAsync(string udid, CancellationToken ct);

    /// <summary>idevicediagnostics ioregentry AppleARMPMUCharger 取电池生产商 / 序列号（仅 iOS 8+ 有节点）。</summary>
    Task<BatterySpec> ReadBatterySpecAsync(string udid, CancellationToken ct);

    /// <summary>
    /// `ideviceinstaller -u &lt;udid&gt; list --xml`，返回**原始 XML 文本**（plist 数组），
    /// 解析交给 ReadbackService（保持可测、可脱离进程单测）。
    /// 超时 30 s（07 §3.3：结果Unknown → 标"Needs confirmation"）；超时 / 启动Failed / 退出码非 0 → 返回 null。
    /// </summary>
    Task<string?> ListInstalledXmlAsync(string udid, CancellationToken ct);
}

/// <summary>
/// AFC 独立进程封装。主程序不直接 P/Invoke AFC（总览 §6.4）。
/// 实现：AfcHelperRunner（启动 AfcHelper.exe、30 s 总时长 + 无输出超时、杀进程树）。
/// </summary>
internal interface IAfcHelper
{
    /// <summary>清空设备 /PublicStaging。清理Failed不阻断安装，只记警告（07 §3.2）。</summary>
    Task<AfcCleanOutcome> CleanStagingAsync(string udid, IProgress<string>? log, CancellationToken ct);

    /// <summary>读设备可用空间（AFC FSTotalBytes / FSFreeBytes），作为 ideviceinfo 的兜底（02 §3）。</summary>
    Task<AfcSpaceOutcome> QuerySpaceAsync(string udid, CancellationToken ct);
}

/// <summary>
/// 回读服务：ideviceinstaller list --xml（总览 §6.10）。
/// 负责解析 + 按 UDID 缓存；安装 / 卸载 / 刷新设备之后调用 <see cref="Invalidate"/> 丢弃缓存。
/// </summary>
internal interface IReadbackService
{
    Task<IReadOnlyList<InstalledApp>> ListInstalledAsync(string udid, CancellationToken ct);

    /// <summary>安装 / 卸载 / 刷新设备之后丢弃该设备的回读缓存，下次强制重新读。</summary>
    void Invalidate(string udid);
}

/// <summary>已安装集合的查询辅助（BundleId 不区分大小写）。</summary>
internal static class InstalledAppSet
{
    public static InstalledApp? Find(IReadOnlyList<InstalledApp>? apps, string bundleId)
    {
        if (apps is null || string.IsNullOrWhiteSpace(bundleId)) return null;
        foreach (var app in apps)
        {
            if (string.Equals(app.BundleId, bundleId, StringComparison.OrdinalIgnoreCase)) return app;
        }
        return null;
    }

    /// <summary>版本比较：任一侧Unknown则返回 null（调用方按"版本Unknown"处理，不判不一致）。</summary>
    public static bool? VersionsMatch(InstalledApp installed, string? packageVersion)
    {
        if (!installed.VersionKnown || string.IsNullOrWhiteSpace(packageVersion)) return null;
        return string.Equals(installed.Version.Trim(), packageVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
