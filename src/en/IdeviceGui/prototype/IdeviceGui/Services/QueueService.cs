using System.Collections.Concurrent;
using System.IO;

namespace IdeviceGui.Services;

// ─────────────────────────────────────────────────────────────────────────────
// 队列事件：QueueService 在后台线程产生，经 IProgress<T> 投递到 UI 线程。
// UI 侧按 JobId 找 JobViewModel 原地改属性（不重建集合，01 §5 / §6.2）。
// ─────────────────────────────────────────────────────────────────────────────

internal abstract record QueueEvent(string Udid);

internal sealed record JobStatusEvent(
    string Udid,
    Guid JobId,
    QueueStatus Status,
    string? Note,
    string? BundleId = null,
    JobKind? Kind = null)
    : QueueEvent(Udid);

internal sealed record JobProgressEvent(string Udid, Guid JobId, double Percent)
    : QueueEvent(Udid);

/// <summary>
/// 安装段进度（2026-11-03 用户定）：解析 CLI 的 `Install: <阶段> (NN%)` 行，0–100。
/// 与 <see cref="JobProgressEvent"/>（传输段 `Transfer: x%`）分开，界面分别显示 `[上传中 NN%]` / `[安装中 NN%]`。
/// </summary>
internal sealed record JobInstallProgressEvent(string Udid, Guid JobId, double Percent)
    : QueueEvent(Udid);

internal sealed record DeviceRunStateEvent(string Udid, QueueRunState State, string? Note)
    : QueueEvent(Udid);

/// <summary>回读结果快照：设备上已安装的 App（BundleId + 版本）。M4 的已安装列与卸载入口读它。</summary>
internal sealed record InstalledAppsEvent(string Udid, IReadOnlyList<InstalledApp> Apps)
    : QueueEvent(Udid);

/// <summary>队列新增/移除（2026-11-07 自动再入队：删旧行、加新行）。</summary>
internal sealed record JobAddedEvent(string Udid, QueueItem Item) : QueueEvent(Udid);
internal sealed record JobRemovedEvent(string Udid, Guid JobId) : QueueEvent(Udid);

/// <summary>队列项的领域对象。不含 INotifyPropertyChanged——界面用 JobViewModel 镜像，两者靠 Id 对应。</summary>
internal sealed class QueueItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Udid { get; init; }
    public required JobKind Kind { get; set; }   // 2026-11-02：同 bundle 复用一行，Kind 会从 Install 切成 Uninstall
    public required string BundleId { get; init; }

    /// <summary>Kind=Install 时必填；Uninstall 时为空。</summary>
    public string? FilePath { get; set; }   // 2026-11-02：同 bundle 复用一行，卸载时清空/切换

    /// <summary>资料库身份的占位：B1 用文件路径，M2 建表后换成 Packages.Id。</summary>
    public string? PackageKey { get; set; }

    /// <summary>IPA 侧版本，用于回读后比对"装的是不是这个版本"；未知为空。</summary>
    public string? PackageVersion { get; set; }

    /// <summary>
    /// IPA 要求的最低 iOS（`MinimumOSVersion`，docs/04）。**任务开始前**用它和设备版本比对：
    /// 不满足直接判失败、不进入上传（2026-10-31 用户要求：不能等传完才报"版本太低"）。null = 未知，不拦。
    /// </summary>
    public string? PackageMinOs { get; set; }

    /// <summary>
    /// 预计解压体积：`.app` 未压缩总量（M3 深解析的 `AppSize`，docs/07 §3.4）。
    /// null / 0 = 未知（包没深解析成功）→ 空间检查按压缩包 ×2 保守估算并记警告。
    /// </summary>
    public long? ExpectedUncompressedBytes { get; set; }

    public int OrderIndex { get; set; }
    public QueueStatus Status { get; set; } = QueueStatus.Pending;
    public string? Note { get; set; }

    /// <summary>单任务取消源（M4-e1）：只取消这一条，与设备级暂停（PumpHandle.Cts）区分开。</summary>
    public CancellationTokenSource? ItemCts { get; set; }

    /// <summary>安装中取消：装完后自动卸载（2026-11-01 用户定）。</summary>
    public bool CancelAfterInstall { get; set; }

    /// <summary>是否还处于上传阶段（看到 <c>Installing '…'</c> 后置 false；只有上传阶段能中止）。每次运行开始时重置。</summary>
    public bool Uploading { get; set; } = true;

    /// <summary>
    /// 安装段进度 0–100（2026-11-03 用户定）：从 CLI 的 `Install: <阶段> (NN%)` 行解析；
    /// 传输阶段为 0。每次运行开始时重置。
    /// </summary>
    public double InstallPercent { get; set; }

    /// <summary>
    /// 入队时该 App 是否已在设备上（null = 未知）。
    /// 覆盖安装取消时**不能自动卸载**——否则会把用户原有的 App（及其被覆盖的数据）一起删掉（审查 P1-2 选项 A）。
    /// </summary>
    public bool? HadInstalledBefore { get; set; }

    /// <summary>
    /// 峰值占用（2026-11-01 实测口径）：压缩包大小 + 解压后 `.app` 占用。
    /// 安装时压缩包（/PublicStaging）与已解压内容会同时存在，峰值 ≈ 两者之和（iPhone 5 / DOOM 实测 173.68 MB）。
    /// 解压体积未知时按压缩包 ×2 估算（2026-11-07）。用于空间检查（+10 MB 余量）与队列排序（降序）。
    /// </summary>
    public long PeakBytes { get; set; }
    public double Percent { get; set; }

    /// <summary>
    /// 该条目曾被中断过（设备离线 / 不可达 / 用户点暂停）。
    /// 恢复执行前要先回读确认"是不是其实已经装好了"，已装且版本一致就不重装（07 §3.5，避免覆盖清数据）。
    /// </summary>
    public bool WasInterrupted { get; set; }

    /// <summary>因设备断开而被批量挂起（2026-11-07）；联机稳定后只恢复这一类，用户手动挂起不动。</summary>
    public bool SuspendedByDisconnect { get; set; }

    /// <summary>自动再入队次数（2026-11-07）：1 = 原始任务；2 = 第二次尝试，依此类推。</summary>
    public int AutoRetryCount { get; set; }

    /// <summary>已确认 App 在设备上但版本不符：禁止自动重装（2026-11-07 审查 P1，防覆盖清数据）。</summary>
    public bool AutoRetrySuppressed { get; set; }
}

/// <summary>入队的处置结果（P1-3：让调用方能区分"新增 / 重置 / 正在跑"）。</summary>
internal enum EnqueueDisposition
{
    /// <summary>
    /// 队列里已有同一个包、但**是另一类任务**（安装 ↔ 卸载），且那条任务还没结束。
    /// 2026-11-03 审查 B2 修复：**不再静默改写 Kind / 清空路径**，由调用方提示"先处理现有任务"。
    /// </summary>
    KindConflict,

    /// <summary>队列里本来没有，新增了一条。</summary>
    Added,

    /// <summary>已有可重置的旧条目：状态复位成待执行并排到队尾。</summary>
    Reset,

    /// <summary>已有条目正在执行：本次不重置（不改状态、不挪队列），调用方应提示"已在处理中"。</summary>
    AlreadyRunning,
}

/// <summary>入队结果：条目本体 + 处置 + 处置时看到的当前状态（日志/提示用）。</summary>
internal readonly record struct EnqueueResult(QueueItem Item, EnqueueDisposition Disposition, QueueStatus CurrentStatus);

/// <summary>状态分类：哪些状态算"在飞"（不能被重新入队重置）。</summary>
internal static class QueueStatusKinds
{
    /// <summary>
    /// 在飞 = 泵正在为这条目的干活。新增状态时必须在这里表态（switch 带 default 抛异常，编译不会漏）。
    /// </summary>
    public static bool IsInFlight(this QueueStatus status) => status switch
    {
        QueueStatus.VerifyDevice
            or QueueStatus.Precheck
            or QueueStatus.Cleaning
            or QueueStatus.SpaceCheck
            or QueueStatus.Installing
            or QueueStatus.Uninstalling
            or QueueStatus.Readback => true,

        QueueStatus.Pending
            or QueueStatus.Suspended
            or QueueStatus.Done
            or QueueStatus.Failed
            or QueueStatus.Paused
            or QueueStatus.Cancelled
            or QueueStatus.NeedsConfirm => false,

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "New queue status not declared (QueueStatusKinds.IsInFlight)"),
    };

    /// <summary>
    /// 能否「取消选中」（2026-11-03 T1 收口）：**与 <see cref="QueueService.Cancel"/> 的 switch 完全一致**。
    /// 注意两处不是纯状态判定：
    /// <list type="bullet">
    /// <item>运行段（Installing / Uninstalling / Readback）只有**安装任务**能取消（装完自动卸载）；卸载任务不可取消；</item>
    /// <item>Paused / NeedsConfirm 不可取消（要取消就先继续；这是设计，不是漏项——见 docs/07 §3.1.1）。</item>
    /// </list>
    /// </summary>
    public static bool IsCancellable(this QueueStatus status, JobKind kind) => status switch
    {
        QueueStatus.Pending
            or QueueStatus.Suspended
            or QueueStatus.VerifyDevice
            or QueueStatus.Precheck
            or QueueStatus.Cleaning
            or QueueStatus.SpaceCheck => true,

        QueueStatus.Installing
            or QueueStatus.Uninstalling
            or QueueStatus.Readback => kind == JobKind.Install,

        QueueStatus.Paused
            or QueueStatus.NeedsConfirm
            or QueueStatus.Done
            or QueueStatus.Failed
            or QueueStatus.Cancelled => false,

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "New queue status not declared (QueueStatusKinds.IsCancellable)"),
    };

    /// <summary>资料库"黄"= 该包还在当前设备队列里（Pending / 在飞 / Paused / NeedsConfirm）。
    /// 等价于 `!IsFinished()`：Failed 也属已结束，不染黄。</summary>
    public static bool IsQueuedMarker(this QueueStatus status) => !status.IsFinished();

    /// <summary>能否重试/继续（与 JobViewModel.CanRetry 同口径）：Failed / Paused / NeedsConfirm。</summary>
    public static bool IsRetryable(this QueueStatus status) => status switch
    {
        QueueStatus.Failed
            or QueueStatus.Paused
            or QueueStatus.NeedsConfirm => true,

        QueueStatus.Pending
            or QueueStatus.Suspended
            or QueueStatus.VerifyDevice
            or QueueStatus.Precheck
            or QueueStatus.Cleaning
            or QueueStatus.SpaceCheck
            or QueueStatus.Installing
            or QueueStatus.Uninstalling
            or QueueStatus.Readback
            or QueueStatus.Done
            or QueueStatus.Cancelled => false,

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "New queue status not declared (QueueStatusKinds.IsRetryable)"),
    };

    /// <summary>
    /// 已结束 = 泵不会再执行、可以被「刷新」清掉（Done / Failed / Cancelled）。
    /// 2026-11-02：界面「清空已结束」与服务端队列清理**共用这一个判定**（新增状态必须在这里表态）。
    /// </summary>
    public static bool IsFinished(this QueueStatus status) => status switch
    {
        QueueStatus.Done
            or QueueStatus.Failed
            or QueueStatus.Cancelled => true,

        QueueStatus.Pending
            or QueueStatus.Suspended
            or QueueStatus.VerifyDevice
            or QueueStatus.Precheck
            or QueueStatus.Cleaning
            or QueueStatus.SpaceCheck
            or QueueStatus.Installing
            or QueueStatus.Uninstalling
            or QueueStatus.Readback
            or QueueStatus.Paused
            or QueueStatus.NeedsConfirm => false,

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "New queue status not declared (QueueStatusKinds.IsFinished)"),
    };
}

/// <summary>
/// 每台设备一条独立队列、一个独立的泵循环（07 §3.1 / §3.8）。
/// 设备之间不共享可变状态、不设全局锁；单设备异常不冒泡到 UI。
/// 本类方法可在任意线程调用；对外只通过 IProgress&lt;QueueEvent&gt; 回报。
/// </summary>
internal sealed class QueueService
{
    private const int InstallIdleTimeoutSeconds = 180;

    /// <summary>空间检查余量（2026-11-01 用户定：按峰值占用 + 10 MB 判断）。</summary>
    private const long SpaceMarginBytes = 10L * 1024 * 1024;

    private readonly ICliRunner _cli;
    private readonly IAfcHelper _afc;
    private readonly IReadbackService? _readback;   // B3 接入；为 null 时跳过 Readback 阶段
    private readonly IProgress<QueueEvent> _events;
    private readonly IProgress<LogLine> _log;

    private readonly ConcurrentDictionary<string, List<QueueItem>> _queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PumpHandle> _pumps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceIdentity> _identities = new(StringComparer.Ordinal);

    /// <summary>
    /// 每个 UDID 一把**自己的**锁，只保护该设备的队列 List。
    /// 这里刻意不用单一全局锁：设备之间必须互不阻塞（硬约束 #3、07 §3.8）。
    /// 锁内只做内存操作，不放 await / IO / 子进程。
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _queueLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// "继续"请求暂存：用户点继续时若该设备的旧泵还在收尾（取消展开中），
    /// 把请求挂在这里，由旧泵的 finally 负责把 Paused 条目重新入队并重启泵。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _resumeRequests = new(StringComparer.Ordinal);

    private volatile bool _shuttingDown;
    private long _version;
    private readonly ConcurrentDictionary<string, byte> _suspended = new(StringComparer.Ordinal);

    /// <summary>失败后最多尝试次数（设置键 Queue.AutoRetryAttempts；默认 2，最低 2）。</summary>
    public int AutoRetryAttempts { get; set; } = 2;

    /// <summary>回读重试间隔（生产 1 s；测试可设 0）。</summary>
    internal TimeSpan ReadbackRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    private const int ReadbackAttempts = 3;

    /// <summary>
    /// 队列实质版本（2026-11-07）：入队 / 状态变化 / 删除 / 恢复排队时递增；进度百分比变化不递增。
    /// 空间预演日志用它 + 装不下任务集合去重。
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>关闭时泵是否超时未退出；为 true 时调用方不要再 Dispose 库（体检报告 P1-5）。</summary>
    public bool ShutdownIncomplete { get; private set; }

    private object GateFor(string udid) => _queueLocks.GetOrAdd(udid, _ => new object());

    /// <summary>峰值占用 = 压缩包 + 解压后（未知则按压缩包 ×2；都未知给 0）。</summary>
    private static long ComputePeakBytes(string? filePath, long? expectedUncompressedBytes)
    {
        long compressed = 0;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try { compressed = new FileInfo(filePath).Length; } catch { /* 文件不可读：按未知处理 */ }
        }

        // 解压体积未知：按压缩包 × 2 保守估算（2026-11-07 用户定）。
        // 入队时算一次，队列排序 / 回读后空间预演 / 执行前空间检查共用，避免三套口径。
        return expectedUncompressedBytes is > 0
            ? compressed + expectedUncompressedBytes.Value
            : compressed * 2;
    }

    /// <summary>
    /// 待执行区重排（2026-11-06 用户定）：**卸载优先**，同类按临时占用（PeakBytes）降序，同占用保持原先后。
    /// 只动 <see cref="QueueStatus.Pending"/>；运行中/已完成/已取消不动。
    /// </summary>
    private static void ReorderPending(List<QueueItem> list)
    {
        var pending = list
            .Where(x => x.Status == QueueStatus.Pending)
            .OrderByDescending(x => x.Kind == JobKind.Uninstall)
            .ThenByDescending(x => x.PeakBytes)
            .ThenBy(x => x.OrderIndex)
            .ToList();

        for (var i = 0; i < pending.Count; i++) pending[i].OrderIndex = i;
    }

    /// <summary>下一个排队序号：同一设备内递增，排除指定条目（upsert 重排时用）。</summary>
    private static int NextOrderIndex(List<QueueItem> list, QueueItem? except = null)
    {
        var max = -1;
        foreach (var item in list)
        {
            if (ReferenceEquals(item, except)) continue;
            if (item.OrderIndex > max) max = item.OrderIndex;
        }
        return max + 1;
    }

    private sealed class PumpHandle
    {
        public CancellationTokenSource Cts { get; init; } = new();
        public Task? Loop { get; set; }
    }

    private readonly record struct DeviceIdentity(string ProductType, string Version, string? Name);

    public QueueService(
        ICliRunner cli,
        IAfcHelper afc,
        IReadbackService? readback,
        IProgress<QueueEvent> events,
        IProgress<LogLine> log)
    {
        _cli = cli;
        _afc = afc;
        _readback = readback;
        _events = events;
        _log = log;
    }

    // b3a：日志按"类别"标注（复审 P0-1 采纳：不再用关键词猜）。分界行必须显式用 LogStart / LogEnd / LogError。
    // b3b：设备信息（名称 + ProductType）走 LogLine 字段，**不再手工拼 `[udid缩写]` 前缀**（复审 P2-E）。
    private LogLine DeviceLine(string udid, string text, LogKind kind)
    {
        _identities.TryGetValue(udid, out var identity);
        return new LogLine(text, kind, udid: udid, deviceName: identity.Name, productType: identity.ProductType);
    }

    private void LogDetail(string udid, string text) => _log.Report(DeviceLine(udid, text, LogKind.Detail));
    private void LogStart(string udid, string text) => _log.Report(DeviceLine(udid, text, LogKind.Start));
    private void LogEnd(string udid, string text) => _log.Report(DeviceLine(udid, text, LogKind.End));
    private void LogError(string udid, string text) => _log.Report(DeviceLine(udid, text, LogKind.Error));

    // 服务级（全局）行：没有设备上下文
    private void LogDetail(string text) => _log.Report(LogLine.Detail(text));

    /// <summary>
    /// 给既有 <c>IProgress&lt;string&gt;</c> 通道（CLI 输出、AFC helper）用的桥：统一按过程行记录；
    /// 简洁模式只显示其中带 <c>WARNING:</c> / <c>ERROR:</c> 的原始行（用户拍板"告警与错误都显示"）。
    /// </summary>
    private IProgress<string> CliLog(string udid, Action<string>? onLine = null) =>
        new StringLogBridge(text =>
        {
            onLine?.Invoke(text);
            LogDetail(udid, text);
        });

    /// <summary>刷新设备后登记该设备的身份基线，任务开始前用它比对（硬约束 #1）。</summary>
    public void SetDeviceIdentity(string udid, string productType, string version, string? name = null)
        => _identities[udid] = new DeviceIdentity(productType, version, name);

    /// <summary>该设备是否已有一条在跑的泵（保证单 UDID 单泵）。</summary>
    public bool IsRunning(string udid) => _pumps.ContainsKey(udid);

    public bool HasPending(string udid)
    {
        if (!_queues.TryGetValue(udid, out var list)) return false;
        lock (GateFor(udid))
        {
            return list.Any(x => x.Status is QueueStatus.Pending);
        }
    }

    public IReadOnlyList<QueueItem> Snapshot(string udid)
    {
        if (!_queues.TryGetValue(udid, out var list)) return Array.Empty<QueueItem>();
        lock (GateFor(udid))
        {
            return list.OrderBy(x => x.OrderIndex).ToList();
        }
    }

    /// <summary>
    /// 移除该设备队列里**已结束**的条目（2026-11-02 用户定：「刷新」= 清空已结束 + 清状态标记，界面与服务端一起清）。
    /// 只删终态（泵永远不会再取它们），锁内只做内存操作；返回移除条数（幂等）。
    /// </summary>
    public int RemoveFinished(string udid)
    {
        if (!_queues.TryGetValue(udid, out var list)) return 0;
        int removed;
        lock (GateFor(udid))
        {
            removed = list.RemoveAll(x => x.Status.IsFinished());
            if (removed > 0) Interlocked.Increment(ref _version);
        }

        // 队列清空（无任何任务）→ 解除挂起；还有任务则保留挂起（2026-11-07 用户定）。
        ClearSuspendIfQueueEmpty(udid, list);
        return removed;
    }

    /// <summary>队列彻底为空时解除队列级挂起并回到空闲；还有任务则保留。</summary>
    private void ClearSuspendIfQueueEmpty(string udid, List<QueueItem> list)
    {
        if (list.Count != 0) return;
        if (!_suspended.TryRemove(udid, out _)) return;
        RaiseRunState(udid, QueueRunState.Idle, null);
        LogDetail(udid, "Queue cleared; suspension lifted");
    }

    /// <summary>
    /// 入队（去重 = upsert）：同设备同包（安装）/ 同设备同 BundleId（卸载）只保留一条，不新增行（07 §3.5）。
    /// <para>
    /// **在飞守卫（P1-3）**：已有条目正在执行时不重置它（否则第二次点击会把状态打回"待执行"，
    /// 随后又在飞流程的完成回报把它覆盖掉，用户看到的是"点了没反应"），而是返回
    /// <see cref="EnqueueDisposition.AlreadyRunning"/> 让调用方提示。
    /// </para>
    /// </summary>
    public EnqueueResult Enqueue(
        string udid,
        JobKind kind,
        string bundleId,
        string? filePath,
        string? packageKey,
        string? packageVersion = null,
        long? expectedUncompressedBytes = null,
        string? packageMinOs = null,
        bool? hadInstalledBefore = null)
    {
        // 峰值占用要读文件元数据：**在锁外算**（硬约束 #3：锁内只做内存操作；审查 N3）
        var peakBytes = ComputePeakBytes(filePath, expectedUncompressedBytes);

        var list = _queues.GetOrAdd(udid, _ => new List<QueueItem>());
        lock (GateFor(udid))
        {
            Interlocked.Increment(ref _version);
            // 2026-11-02 用户定：**一个包（BundleId）在队列里只有一行**——
            // 同 bundle 的安装/卸载互相复用同一行（卸载会覆盖原来的"安装成功"行，而不是新增）。
            var existing = list.FirstOrDefault(x =>
                string.Equals(x.BundleId, bundleId, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                if (existing.Status.IsInFlight())
                {
                    // 在飞：一律不动（不改状态 / 不挪队列 / 不覆盖 WasInterrupted），交给调用方提示
                    return new EnqueueResult(existing, EnqueueDisposition.AlreadyRunning, existing.Status);
                }

                // 审查 B2 修复（2026-11-03）：同一个包安装/卸载**互相覆盖**会造成语义突变
                // （"待安装"被改成"卸载"，路径/版本被清空）。还没结束的异类任务一律不动，让调用方提示。
                if (existing.Kind != kind && !existing.Status.IsFinished())
                {
                    return new EnqueueResult(existing, EnqueueDisposition.KindConflict, existing.Status);
                }

                // upsert：重置状态 + 排到队尾，不新增行（07 §3.5）；Kind/路径也一起换成这次要做的任务
                existing.Kind = kind;
                existing.FilePath = filePath;
                existing.PackageKey = packageKey;
                existing.AutoRetryCount = 1;
                existing.Status = _suspended.ContainsKey(udid) ? QueueStatus.Suspended : QueueStatus.Pending;
                existing.Note = existing.Status == QueueStatus.Suspended ? "Queue suspended" : null;
                existing.Percent = 0;
                existing.InstallPercent = 0;
                existing.WasInterrupted = false;
                existing.ExpectedUncompressedBytes = expectedUncompressedBytes;   // 重新解析过就带上新值
                existing.PeakBytes = peakBytes;
                // 审查 P1-1：这些"每次运行"的状态必须重置，否则二次取消上传会被当成"已进入安装段 → 装完再卸载"
                existing.Uploading = true;
                existing.CancelAfterInstall = false;
                existing.ItemCts = null;
                existing.HadInstalledBefore = hadInstalledBefore;
                // 复审 B2：同路径重入队（比如包被换版本）必须刷新，否则泵内用旧 MinOs / 旧版本比对
                existing.PackageVersion = packageVersion;
                existing.PackageMinOs = packageMinOs;
                existing.OrderIndex = NextOrderIndex(list, except: existing);
                ReorderPending(list);
                return new EnqueueResult(existing, EnqueueDisposition.Reset, existing.Status);
            }

            var item = new QueueItem
            {
                Udid = udid,
                Kind = kind,
                BundleId = bundleId,
                FilePath = filePath,
                PackageKey = packageKey,
                PackageVersion = packageVersion,
                ExpectedUncompressedBytes = expectedUncompressedBytes,
                PackageMinOs = packageMinOs,
                OrderIndex = NextOrderIndex(list),
                PeakBytes = peakBytes,
                HadInstalledBefore = hadInstalledBefore,
                Status = _suspended.ContainsKey(udid) ? QueueStatus.Suspended : QueueStatus.Pending,
                Note = _suspended.ContainsKey(udid) ? "Queue suspended" : null,
                AutoRetryCount = 1,
            };
            list.Add(item);
            ReorderPending(list);
            return new EnqueueResult(item, EnqueueDisposition.Added, item.Status);
        }
    }

    /// <summary>
    /// 启动该设备的泵；已有一条在跑时直接返回（单 UDID 单泵）。
    /// <para>
    /// 竞态处理：暂停后**立刻**点"继续"时，旧泵可能已经跑完但还没走到 <c>finally</c> 把自己从
    /// <see cref="_pumps"/> 里移除；这时直接 <c>TryAdd</c> 会失败，新泵起不来、队列看起来"卡住"。
    /// 所以遇到"Loop 已完成"的旧句柄时先把它清掉再试一次。
    /// </para>
    /// </summary>
    public void StartPump(string udid)
    {
        if (_suspended.ContainsKey(udid)) return;
        while (true)
        {
            if (_pumps.TryGetValue(udid, out var existing))
            {
                if (existing.Loop is { IsCompleted: true })
                {
                    // 旧泵已结束（只差 finally 没执行）：原子地移除它再重试
                    if (_pumps.TryRemove(new KeyValuePair<string, PumpHandle>(udid, existing)))
                    {
                        try { existing.Cts.Dispose(); } catch { }
                        continue;
                    }

                    return;
                }

                return;   // 真的在跑：保持单泵
            }

            var handle = new PumpHandle();
            if (!_pumps.TryAdd(udid, handle))
            {
                handle.Cts.Dispose();
                continue;   // 被别人抢先插入：重来一轮判断
            }

            handle.Loop = Task.Run(() => PumpLoopAsync(udid, handle));
            return;
        }
    }

    /// <summary>
    /// 用户点"继续"：把 Paused 的条目放回 Pending，重新起泵。
    /// <para>
    /// 必须处理一个时序窗口（B6 单元测试抓到的真实缺陷）：用户点了暂停、取消还在展开（真实场景要杀进程树，
    /// 有几十~几百 ms），此时条目还停在 <c>Installing</c> 而不是 <c>Paused</c>，旧泵也还没进 finally。
    /// 如果这时直接 <c>StartPump</c>，会被"单泵保护"挡掉；旧泵收尾后把条目设成 Paused 就再没人管，
    /// 队列永久卡住。做法：记一个"收尾后重启"的请求，由旧泵的 finally 接手重启。
    /// </para>
    /// </summary>
    public void Resume(string udid)
    {
        _suspended.TryRemove(udid, out _);
        RequeuePausedItems(udid);
        EnsurePump(udid);
    }

    /// <summary>
    /// 旧泵交接（2026-11-07 审查 P1）：旧泵还在收尾时交给它的 finally 重启，
    /// 避免 StartPump 被单泵保护静默 return、Pending 永远没人执行。
    /// </summary>
    private void EnsurePump(string udid)
    {
        if (_pumps.ContainsKey(udid))
        {
            _resumeRequests[udid] = 1;
            if (_pumps.ContainsKey(udid)) return;
            _resumeRequests.TryRemove(udid, out _);
        }

        StartPump(udid);
    }

    /// <summary>把该设备所有 Paused 条目放回 Pending（"继续"的公共动作）。</summary>
    private void RequeuePausedItems(string udid)
    {
        var list = _queues.GetOrAdd(udid, _ => new List<QueueItem>());
        lock (GateFor(udid))
        {
            foreach (var item in list.Where(x => x.Status is QueueStatus.Paused or QueueStatus.NeedsConfirm or QueueStatus.Suspended))
            {
                item.Status = QueueStatus.Pending;
                item.SuspendedByDisconnect = false;
                item.Note = null;
                item.Percent = 0;
                item.InstallPercent = 0;
                RaiseStatus(udid, item);
            }

            // 恢复后必须重新按“卸载优先 + 临时占用降序”排，否则暂停过的卸载会留在安装后面。
            ReorderPending(list);
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>
    /// 队列挂起（2026-11-07 用户定）：当前任务不打断；所有 Pending 置 Suspended（灰色）；
    /// 当前任务结束后泵不再取下一个。挂起期间新入队也直接 Suspended。
    /// </summary>
    public void Suspend(string udid)
    {
        _suspended[udid] = 1;
        var list = _queues.GetOrAdd(udid, _ => new List<QueueItem>());
        var hasInFlight = false;
        lock (GateFor(udid))
        {
            hasInFlight = list.Any(x => x.Status.IsInFlight());
            foreach (var item in list.Where(x => x.Status == QueueStatus.Pending))
            {
                item.Status = QueueStatus.Suspended;
                item.SuspendedByDisconnect = false;
                item.Note = "Queue suspended";
                RaiseStatus(udid, item);
            }

            Interlocked.Increment(ref _version);
        }

        if (hasInFlight)
        {
            // 当前任务还在跑：保持"运行中"，等它结束后泵再报 Suspended。
            LogEnd(udid, "Queue suspended: current task continues; no next task will start");
        }
        else
        {
            RaiseRunState(udid, QueueRunState.Suspended, "Suspended");
            LogEnd(udid, "Queue suspended: next task is on hold");
        }
    }

    /// <summary>断开后暂停：当前任务已 Paused，其余 Pending 一起转挂起并打标记（2026-11-07）。</summary>
    private void SuspendPendingForDisconnect(string udid)
    {
        if (!_queues.TryGetValue(udid, out var list)) return;
        lock (GateFor(udid))
        {
            var changed = false;
            foreach (var item in list.Where(x => x.Status == QueueStatus.Pending))
            {
                item.Status = QueueStatus.Suspended;
                item.SuspendedByDisconnect = true;
                item.Note = "Suspended (disconnected)";
                RaiseStatus(udid, item);
                changed = true;
            }

            if (changed) Interlocked.Increment(ref _version);
        }
    }

    /// <summary>
    /// 联机稳定后恢复：只恢复「Suspended (disconnected)」与中断 Paused；用户手动挂起的 Suspended 不动。
    /// 用户手动挂起期间不自动恢复。
    /// </summary>
    public int ResumeAfterReconnect(string udid)
    {
        if (_suspended.ContainsKey(udid)) return 0;
        if (!_queues.TryGetValue(udid, out var list)) return 0;
        var restored = 0;
        lock (GateFor(udid))
        {
            foreach (var item in list.Where(x =>
                         x.Status == QueueStatus.Paused ||
                         (x.Status == QueueStatus.Suspended && x.SuspendedByDisconnect)))
            {
                item.Status = QueueStatus.Pending;
                item.SuspendedByDisconnect = false;
                item.Note = null;
                item.Percent = 0;
                item.InstallPercent = 0;
                RaiseStatus(udid, item);
                restored++;
            }

            ReorderPending(list);
            Interlocked.Increment(ref _version);
        }

        if (restored > 0) EnsurePump(udid);
        return restored;
    }

    /// <summary>用户点"暂停"：取消当前泵并停在与该任务上（07 §3.7）。仅为内部/测试保留，UI 不再暴露。</summary>
    public void Pause(string udid)
    {
        if (!_pumps.TryGetValue(udid, out var handle)) return;
        try { handle.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 取消一个**还没开始**的排队任务（2026-11-01 用户定：只有已排队且通过检查的 Pending 能立刻取消）。
    /// 返回 false = 条目不存在或已经不在 Pending（开始跑/已完成），调用方据此提示。
    /// <para>上传中/安装中的取消不走这里：上传中 = 杀进程树，安装中 = 装完自动卸载（M4-e1 后续步骤）。</para>
    /// </summary>
    public bool CancelPending(string udid, Guid itemId)
    {
        if (!_queues.TryGetValue(udid, out var list)) return false;

        QueueItem? target;
        lock (GateFor(udid))
        {
            target = list.FirstOrDefault(x => x.Id == itemId);
            if (target is null || target.Status is not (QueueStatus.Pending or QueueStatus.Suspended)) return false;
        }

        SetStatus(udid, target, QueueStatus.Cancelled, "Cancelled by user (not started)");
        LogDetail(udid, $"Cancelled (not started): {DisplayName(target)}");
        return true;
    }

    /// <summary>取消的结果（M4-e1）：调用方据此给用户提示。</summary>
    public enum CancelOutcome
    {
        NotFound,
        AlreadyCancelled,
        CancelledPending,
        CancelledRunning,
        ScheduledAfterInstall,
        NotCancellable,
    }

    /// <summary>
    /// 取消一个任务（2026-11-01 用户定稿）：
    /// <list type="bullet">
    /// <item>Pending → 立刻取消；</item>
    /// <item>上传阶段（安装开始前 / 看到 <c>Transfer</c> 但还没 <c>Installing</c>）→ 杀进程树（走单任务 CTS），记已取消；</item>
    /// <item>安装段 / 回读 → 不能中止，标记「装完自动卸载」；</item>
    /// <item>已取消 → 返回 AlreadyCancelled。</item>
    /// </list>
    /// </summary>
    public CancelOutcome Cancel(string udid, Guid itemId)
    {
        if (!_queues.TryGetValue(udid, out var list)) return CancelOutcome.NotFound;

        // 审查 P2-4：状态判定与写入放进同一把锁（CancelPending 内部同线程重入 lock 安全）
        lock (GateFor(udid))
        {
            var item = list.FirstOrDefault(x => x.Id == itemId);
            if (item is null) return CancelOutcome.NotFound;

            // T1：取消能力只由 QueueStatusKinds.IsCancellable 决定（UI 的「取消选中」同一出口）
            if (!item.Status.IsCancellable(item.Kind))
                return item.Status == QueueStatus.Cancelled ? CancelOutcome.AlreadyCancelled : CancelOutcome.NotCancellable;

            switch (item.Status)
            {
                case QueueStatus.Pending:
                case QueueStatus.Suspended:
                    return CancelPending(udid, itemId) ? CancelOutcome.CancelledPending : CancelOutcome.NotFound;

                case QueueStatus.VerifyDevice:
                case QueueStatus.Precheck:
                case QueueStatus.Cleaning:
                case QueueStatus.SpaceCheck:
                case QueueStatus.Installing when item.Uploading:
                    try { item.ItemCts?.Cancel(); } catch (ObjectDisposedException) { }
                    LogDetail(udid, $"Cancel requested: {DisplayName(item)} (succeeded, process terminated)");
                    return CancelOutcome.CancelledRunning;

                case QueueStatus.Installing:
                case QueueStatus.Uninstalling:
                case QueueStatus.Readback:
                    if (item.CancelAfterInstall) return CancelOutcome.ScheduledAfterInstall;
                    item.CancelAfterInstall = true;
                    LogDetail(udid, $"Cancel requested: {DisplayName(item)} entered the install phase; it will be uninstalled automatically");
                    return CancelOutcome.ScheduledAfterInstall;

                default:
                    // 不改 Kind/status 的形状；走到这里说明 IsCancellable 与 switch 漂移了
                    throw new ArgumentOutOfRangeException(
                        nameof(itemId), item.Status, "Cancel branch does not match QueueStatusKinds.IsCancellable (T1)");
            }
        }
    }

    /// <summary>安装日志里出现 <c>Installing '…'</c> → 进入安装段，之后不可中止。</summary>
    private static void MarkInstallStarted(QueueItem item, string line)
    {
        if (line.StartsWith("Installing", StringComparison.OrdinalIgnoreCase)) item.Uploading = false;
    }

    /// <summary>
    /// 安装段的每一行：① 认出 `Installing '…'`（上传结束）；② 认出 `Install: <阶段> (NN%)` → 记进度并投递事件
    /// （2026-11-03 用户定：队列行显示 `[安装中 NN%]`，页签条后 40% 也按它走）。
    /// </summary>
    private void OnInstallLine(string udid, QueueItem item, string line)
    {
        MarkInstallStarted(item, line);
        if (!CliRunner.TryParseInstallPercent(line, out var percent)) return;

        item.InstallPercent = percent;
        _events.Report(new JobInstallProgressEvent(udid, item.Id, percent));
    }

    /// <summary>安装中取消 → 装完后自动卸载（复用卸载链路：校验设备 + 卸载前回读 + 卸载）。</summary>
    private async Task RunAutoUninstallAsync(string udid, QueueItem original, CancellationToken ct)
    {
        original.CancelAfterInstall = false;

        // 审查 P1-2（选项 A）：装前已经存在同一个 App → 这次是覆盖安装、原有数据已被清，
        // 再自动卸载会把用户的 App 一起删掉（不可逆）。此时不卸载，只如实记 Note。
        if (original.HadInstalledBefore == true)
        {
            original.Note = "Overwrite install completed (original data cleared); not auto-uninstalled";
            LogDetail(udid, $"Installed without auto-uninstall (already present before install): {DisplayName(original)}");
            original.Status = QueueStatus.Cancelled;
            RaiseStatus(udid, original);
            return;
        }

        // 去重：同设备已有同 BundleId 的卸载任务在排队/在跑 → 不重复卸（审查 P2-1）
        if (_queues.TryGetValue(udid, out var list))
        {
            lock (GateFor(udid))
            {
                var duplicate = list.FirstOrDefault(x =>
                    x.Kind == JobKind.Uninstall &&
                    string.Equals(x.BundleId, original.BundleId, StringComparison.OrdinalIgnoreCase) &&
                    !x.Status.IsFinished());
                if (duplicate is not null)
                {
                    original.Note = "Cancelled (an uninstall task is already queued; not queued again)";
                    original.Status = QueueStatus.Cancelled;
                    RaiseStatus(udid, original);
                    return;
                }
            }
        }

        var uninstall = new QueueItem
        {
            Udid = udid,
            Kind = JobKind.Uninstall,
            BundleId = original.BundleId,
        };

        // 自动卸载期间在原行给出可见状态（审查 P2-1：临时条目不在队列里，UI 看不到进度）
        original.Note = "Uninstalling automatically...";
        RaiseStatus(udid, original);
        LogDetail(udid, $"Auto-uninstall after install: {DisplayName(original)} (cancel request will run afterwards)");

        var paused = await RunItemAsync(udid, uninstall, ct).ConfigureAwait(false);
        original.Note = paused
            ? "Installed, but device is offline; auto-uninstall did not complete"
            : uninstall.Status == QueueStatus.Done
                ? "Cancelled (uninstalled after install)"
                : "Installed · auto-uninstall not completed: " + (uninstall.Note ?? uninstall.Status.ToText());
        original.Status = QueueStatus.Cancelled;
        RaiseStatus(udid, original);
    }

    /// <summary>关窗时调用：取消所有循环与子进程，确保没有残留（B1 判据）。</summary>
    public async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        _shuttingDown = true;
        var handles = _pumps.Values.ToArray();

        foreach (var h in handles)
        {
            try { h.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        var loops = handles.Where(h => h.Loop is not null).Select(h => h.Loop!).ToArray();
        if (loops.Length > 0)
        {
            try
            {
                var all = Task.WhenAll(loops);
                var finished = await Task.WhenAny(all, Task.Delay(timeout ?? TimeSpan.FromSeconds(8))).ConfigureAwait(false);
                if (!ReferenceEquals(finished, all) || !all.IsCompletedSuccessfully)
                {
                    // 有泵没在宽限内退出：库/日志可能还被它用着，调用方不要再 Dispose（进程即将退出）
                    ShutdownIncomplete = true;
                    LogDetail("Queue shutdown timed out: pumps still running; skipped explicit release of database and log");
                }
            }
            catch
            {
                // 关窗路径不再抛异常
            }
        }

        foreach (var h in handles)
        {
            try { h.Cts.Dispose(); } catch { }
        }
        _pumps.Clear();
    }

    // ── 泵循环 ────────────────────────────────────────────────────────────────

    private async Task PumpLoopAsync(string udid, PumpHandle handle)
    {
        var ct = handle.Cts.Token;
        QueueItem? current = null;
        var devicePaused = false;
        var suspended = false;
        try
        {
            RaiseRunState(udid, QueueRunState.Running, null);

            while (!ct.IsCancellationRequested)
            {
                if (_suspended.ContainsKey(udid)) { suspended = true; break; }
                current = TakeNext(udid);
                if (current is null)
                {
                    if (_suspended.ContainsKey(udid)) suspended = true;
                    break;
                }

                // 挂起恰好在 TakeNext 之后、真正执行之前：把这条也转为挂起，不启动。
                if (_suspended.ContainsKey(udid))
                {
                    SetStatus(udid, current, QueueStatus.Suspended, "Queue suspended");
                    suspended = true;
                    current = null;
                    break;
                }

                using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                current.ItemCts = itemCts;
                bool paused;
                try
                {
                    paused = await RunItemAsync(udid, current, itemCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (itemCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // 单任务取消（上传阶段被杀 / 安装前）：不算失败，不触发设备离线判定
                    current.Status = QueueStatus.Cancelled;
                    current.Note = "Cancelled (upload interrupted)";
                    current.WasInterrupted = false;
                    RaiseStatus(udid, current);
                    LogDetail(udid, $"Cancelled (upload interrupted): {DisplayName(current)}; leftover files not cleaned up");
                    current.ItemCts = null;
                    current = null;
                    continue;
                }
                finally
                {
                    if (current is not null) current.ItemCts = null;
                }

                if (current.CancelAfterInstall && current.Status == QueueStatus.Done && !ct.IsCancellationRequested)
                {
                    await RunAutoUninstallAsync(udid, current, ct).ConfigureAwait(false);
                }

                // 2026-11-07：失败后按设置次数自动再入队（删旧行、只留新行）。
                if (current.Status == QueueStatus.Failed && !ct.IsCancellationRequested &&
                    !current.AutoRetrySuppressed &&
                    current.AutoRetryCount < Math.Max(2, AutoRetryAttempts))
                {
                    if (TryAutoRequeueFailed(udid, current))
                    {
                        current = null;
                        continue;
                    }
                }

                current = null;
                if (paused)
                {
                    // 设备级暂停：泵停住等用户点继续，不能被后面的"空闲"覆盖
                    devicePaused = true;
                    break;
                }
            }

            if (suspended || _suspended.ContainsKey(udid))
            {
                RaiseRunState(udid, QueueRunState.Suspended, "Suspended; will stop after the current task");
            }
            else if (devicePaused)
            {
                // 设备级暂停已有终态；不要用"空闲"覆盖它
            }
            else if (ct.IsCancellationRequested)
            {
                // 暂停恰好落在泵的空闲窗口（current 为 null）：也要发终态，否则界面永远显示"运行中"、
                // "继续"永远不可点，Pending 条目也没人泵（体检报告 P1-2）。
                RaiseRunState(udid, QueueRunState.Paused, _shuttingDown ? "App closed; cancelled" : "Paused by user");
            }
            else
            {
                RaiseRunState(udid, QueueRunState.Idle, null);
            }
        }
        catch (OperationCanceledException)
        {
            // Paused by user / 关窗：把当前正在跑的条目停到 Paused，供"继续"恢复（07 §3.7）
            // T1：原来漏了 Cancelled——用户已取消的条目会被暂停异常改回 Paused（语义丢失）
            if (current is not null && !current.Status.IsFinished())
            {
                // 被中断过：用户点"继续"后要先回读确认是否其实已装好，避免覆盖重装清数据（07 §3.5）
                current.WasInterrupted = true;
                var note = _shuttingDown ? "App closed; cancelled" : "Paused by user";
                current.Status = QueueStatus.Paused;
                current.Note = note;
                RaiseStatus(udid, current);
                RaiseRunState(udid, QueueRunState.Paused, note);
            }
        }
        catch (Exception ex)
        {
            // 单设备异常只影响该设备（硬约束 #3）
            LogError(udid, $"Queue error: {ex.Message}");
            RaiseRunState(udid, QueueRunState.Idle, "Queue error: " + ex.Message);
        }
        finally
        {
            // 只移除"自己这一个句柄"：暂停后立刻继续时，新的泵可能已经顶上来了，
            // 无条件 TryRemove(udid) 会把新泵的句柄删掉，造成下一个任务没人泵。
            if (_pumps.TryGetValue(udid, out var registered) && ReferenceEquals(registered, handle))
            {
                _pumps.TryRemove(udid, out _);
            }

            // 暂停后立刻继续：旧泵收尾时若还挂着"继续"请求，重新入队并重启
            // （旧泵的 catch 可能刚把条目设成 Paused，所以这里要再 requeue 一次）
            if (!_shuttingDown && _resumeRequests.TryRemove(udid, out _))
            {
                RequeuePausedItems(udid);
                StartPump(udid);
            }
        }
    }

    private QueueItem? TakeNext(string udid)
    {
        if (!_queues.TryGetValue(udid, out var list)) return null;
        lock (GateFor(udid))
        {
            return list
                .Where(x => x.Status == QueueStatus.Pending)
                .OrderBy(x => x.OrderIndex)
                .FirstOrDefault();
        }
    }

    /// <summary>把服务端当前 OrderIndex 快照给界面，保证队列面板与实际待执行顺序一致。</summary>
    internal IReadOnlyDictionary<Guid, int> GetOrderIndexes(string udid)
    {
        if (!_queues.TryGetValue(udid, out var list))
            return new Dictionary<Guid, int>();
        lock (GateFor(udid))
            return list.ToDictionary(x => x.Id, x => x.OrderIndex);
    }

    /// <summary>返回 true = 该设备需要暂停（离线 / 不可达 / 身份不符），泵循环应停住等用户继续。</summary>
    private async Task<bool> RunItemAsync(string udid, QueueItem item, CancellationToken ct)
    {
        // 每次开始执行都重置"本次运行"的取消状态（审查 P1-1）：Uploading 必须回到 true，
        // 否则装过一次的条目再次入队后，上传阶段的取消会被误判成"已进入安装段"。
        item.Uploading = true;
        item.CancelAfterInstall = false;

        // 安装链路按 docs/07 §3.9 的权威步骤 1–10 拆成 6 个私有方法（T2，2026-11-03：纯提取，语义不变）。
        // null = 本步通过、继续；true = 整台设备暂停；false = 该条目结束（完成/失败/需确认）。
        if (await PrepareAsync(udid, item, ct).ConfigureAwait(false) is { } prepared) return prepared;         // 步骤 1–4
        if (await PrecheckAsync(udid, item, ct).ConfigureAwait(false) is { } prechecked) return prechecked;     // 步骤 5
        if (await CleanStagingAsync(udid, item, ct).ConfigureAwait(false) is { } cleaned) return cleaned;       // 步骤 6
        if (await CheckSpaceStepAsync(udid, item, ct).ConfigureAwait(false) is { } space) return space;         // 步骤 7
        if (await ExecuteCliAsync(udid, item, ct).ConfigureAwait(false) is { } executed) return executed;       // 步骤 8
        return await VerifyAndCompleteAsync(udid, item, ct).ConfigureAwait(false);                              // 步骤 9–10
    }

    /// <summary>步骤 1–4：设备校验 → 中断恢复回读 → MinOs 版本闸门 → 源文件检查。null = 继续。</summary>
    private async Task<bool?> PrepareAsync(string udid, QueueItem item, CancellationToken ct)
    {
        // 1 任务开始前校验设备（硬约束 #1 的第三处校验点）
        SetStatus(udid, item, QueueStatus.VerifyDevice);
        DeviceProbe probe;
        try
        {
            probe = await _cli.ProbeAsync(udid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return PauseDevice(udid, item, "Could not probe device: " + ex.Message);
        }

        if (!probe.Present)
            return PauseDevice(udid, item, "Device offline", disconnect: true);

        if (_identities.TryGetValue(udid, out var expected))
        {
            if (!string.Equals(probe.ProductType, expected.ProductType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(probe.Version, expected.Version, StringComparison.OrdinalIgnoreCase))
            {
                return PauseDevice(udid, item,
                    $"Device identity mismatch (recorded {expected.ProductType}/iOS {expected.Version}, " +
                    $"actual {probe.ProductType}/iOS {probe.Version})");
            }
        }

        // 2026-11-05：瞬间入队后，覆盖判断必须在安装前取；复用 ReadbackService 的 30 s 缓存，
        // 缓存未命中才真正回读一次。失败保持 null → 自动卸载按“未装”处理（会卸载）；
        // 只有 true 才代表装前已有同 BundleId，自动卸载时跳过、避免删掉用户原 App。
        if (item.Kind == JobKind.Install && !item.WasInterrupted &&
            item.HadInstalledBefore is null && _readback is not null)
        {
            SetStatus(udid, item, QueueStatus.Readback);
            try
            {
                var installedBefore = await _readback.ListInstalledAsync(udid, ct).ConfigureAwait(false);
                RaiseInstalledApps(udid, installedBefore);
                item.HadInstalledBefore = InstalledAppSet.Find(installedBefore, item.BundleId) is not null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogDetail(udid, $"Pre-install readback incomplete: {ex.Message} (overwrite check treated as unknown)");
            }
        }

        // 2 恢复条目（曾被中断）：执行前先回读，已装且版本一致就不重装（07 §3.5）
        //     这是"断联/关程序后其实已经装好了"的唯一识别手段，也避免覆盖安装把数据清掉。
        if (item.Kind == JobKind.Install && item.WasInterrupted && _readback is not null)
        {
            SetStatus(udid, item, QueueStatus.Readback);
            var recovery = await TryRecoverAlreadyInstalledAsync(udid, item, ct).ConfigureAwait(false);
            if (recovery == RecoveryOutcome.AlreadyInstalled) return false;

            if (recovery == RecoveryOutcome.Unknown)
            {
                // 回读失败 ≠ 没装：装下去会触发覆盖安装、重建容器清空 App 数据（AGENTS §5.7）。
                // 设备在线 → 标"需确认"等用户决定；离线 → 暂停（保持 WasInterrupted）。
                var after = await SafeProbeAsync(udid, ct).ConfigureAwait(false);
                if (!after.Present)
                    return PauseDevice(udid, item, "Pre-resume readback incomplete; device offline (result unknown)", disconnect: true);

                return FailItem(udid, item, "Pre-resume readback incomplete; could not verify result");
            }

            // NotInstalled → 继续正常安装流程
        }

        // 3 版本前置校验（2026-10-31 用户要求，复审 B1 采纳挪到这里）：
        //     **先**走恢复回读——"上次中断但已装好"的条目应先记完成（避免覆盖清数据），
        //     确认没装再比 MinOs：不满足直接失败，不进入上传。
        if (item.Kind == JobKind.Install &&
            VersionRules.CheckMinimumOs(item.PackageMinOs, probe.Version) is { } minOsError)
        {
            return FailItem(udid, item, "Version requirement not met: " + minOsError + " (not uploaded, forced install may be attempted)");
        }

        // 4 执行前检查源文件（用户 2026-10-30 拍板；判据与 MainViewModel 入队检查同步维护）：
        // 存在 + 可读（被占用等 200 ms 重试一次），失败就秒判 Failed，不再拉起 ideviceinstaller 空跑 2–4 秒。
        if (item.Kind == JobKind.Install)
        {
            var unusable = await CheckPackageFileAsync(item.FilePath, ct).ConfigureAwait(false);
            if (unusable is not null)
                return FailItem(udid, item, unusable);
        }

        return null;
    }

    /// <summary>步骤 5：预检（BundleId / 卸载前确认目标存在）。null = 继续。</summary>
    private async Task<bool?> PrecheckAsync(string udid, QueueItem item, CancellationToken ct)
    {
        // 5 预检：文件存在、包已解析出 BundleId（07 §2）；卸载要确认目标确实在设备上
        SetStatus(udid, item, QueueStatus.Precheck);
        if (item.Kind == JobKind.Install)
        {
            if (string.IsNullOrWhiteSpace(item.BundleId))
                return FailItem(udid, item, "No BundleId parsed; cannot queue install");

            // 文件存在 + 可读已经在 ①.9 检查过（CheckPackageFileAsync），这里不再重复 File.Exists（审核 P2-5）
        }
        else if (item.Kind == JobKind.Uninstall && _readback is not null)
        {
            // iOS 6/7 卸载不存在的 App 会返回 0（假成功，总览 §6.3），只靠退出码判不出来。
            // 判法："开始前确认在装 + 结束后确认消失"，两步都要有。
            try
            {
                _readback.Invalidate(udid);
                var installedBefore = await _readback.ListInstalledAsync(udid, ct).ConfigureAwait(false);
                RaiseInstalledApps(udid, installedBefore);
                if (InstalledAppSet.Find(installedBefore, item.BundleId) is null)
                    return FailItem(udid, item, $"{item.BundleId} is not on the device; nothing to uninstall");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogDetail(udid, $"Pre-uninstall readback incomplete: {ex.Message} (could not confirm the target exists; continuing)");
            }
        }

        return null;
    }

    /// <summary>步骤 6：清理 /PublicStaging（仅安装；失败不阻断）。null = 继续。</summary>
    private async Task<bool?> CleanStagingAsync(string udid, QueueItem item, CancellationToken ct)
    {
        // 6 清理 /PublicStaging（仅安装；清理失败不阻断）
        if (item.Kind == JobKind.Install)
        {
            SetStatus(udid, item, QueueStatus.Cleaning);
            AfcCleanOutcome clean;
            try
            {
                clean = await _afc.CleanStagingAsync(udid, CliLog(udid), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return PauseDevice(udid, item, "AFC cleanup error: " + ex.Message);
            }

            if (clean.TimedOut)
                return PauseDevice(udid, item, "AFC helper timed out; process terminated: device unreachable", disconnect: true);
            if (clean.DeviceUnreachable)
                return PauseDevice(udid, item, "AFC could not connect to the device" + (clean.Warning is null ? "" : "：" + clean.Warning), disconnect: true);
            if (clean.Warning is not null)
            {
                LogEnd(udid, $"Warning: {clean.Warning} (continuing install)");
                item.Note = "Warning: " + clean.Warning;
            }
            LogEnd(udid, $"Cleanup done; {clean.Removed} items removed");
        }

        return null;
    }

    /// <summary>步骤 7：空间检查（清理之后）。null = 继续。</summary>
    private async Task<bool?> CheckSpaceStepAsync(string udid, QueueItem item, CancellationToken ct)
    {
        // 7 空间检查（清理之后，07 §3.1）
        if (item.Kind == JobKind.Install)
        {
            SetStatus(udid, item, QueueStatus.SpaceCheck);
            var spaceNote = await CheckSpaceAsync(udid, item, ct).ConfigureAwait(false);
            if (spaceNote is not null)
                return SuspendForSpace(udid, item, spaceNote + "\nPlease free up space and try again");
        }

        return null;
    }

    /// <summary>步骤 8：执行 CLI（安装 / 卸载；含超时、断联、失败后回读确认）。null = 继续。</summary>
    private async Task<bool?> ExecuteCliAsync(string udid, QueueItem item, CancellationToken ct)
    {
        // 8 执行
        if (item.Kind == JobKind.Install)
        {
            SetStatus(udid, item, QueueStatus.Installing);
            LogStart(udid, $"Installing {Path.GetFileName(item.FilePath)}");

            InstallOutcome outcome;
            var percent = new ThrottledProgress<double>(p => RaiseProgress(udid, item, p));
            try
            {
                outcome = await _cli
                    .InstallAsync(udid, item.FilePath!, CliLog(udid, line => OnInstallLine(udid, item, line)), percent, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return FailItem(udid, item, "Install error: " + ex.Message);
            }

            if (outcome.TimedOut)
            {
                // 杀完再探测：离线 → 暂停；在线 → 该包记失败，继续下一个（07 §3.3）
                var after = await SafeProbeAsync(udid, ct).ConfigureAwait(false);
                if (!after.Present)
                    return PauseDevice(udid, item, "Install timed out; device went offline", disconnect: true);
                return FailItem(udid, item, $"Install timed out: no output for {InstallIdleTimeoutSeconds} s; process terminated");
            }

            if (!outcome.LooksSuccessful)
            {
                // 失败后必须再探测一次：拔线/掉线也会让 install 以 ERROR 退出，
                // 这种"其实设备不在了"要走 Paused 等重连，不能当普通坏包记 Failed（07 §3.1）。
                var after = await SafeProbeAsync(udid, ct).ConfigureAwait(false);
                var reason = outcome.FirstError ?? ("Exit code " + outcome.ExitCode);
                if (!after.Present)
                    return PauseDevice(udid, item, "Device offline (install interrupted): " + reason, disconnect: true);

                // "其实装好了"：上传中断 / 断联 / 假报错都可能让 CLI 报错但 App 已装上（总览 §6.1）
                var confirmed = await TryConfirmInstalledAfterFailureAsync(udid, item, ct).ConfigureAwait(false);
                if (confirmed is not null)
                    return CompleteItem(udid, item, $"Install reported an error ({reason}), but readback confirmed the install{confirmed}");

                return FailItem(udid, item, "Install incomplete: " + reason);
            }
        }
        else
        {
            SetStatus(udid, item, QueueStatus.Uninstalling);
            LogStart(udid, $"Uninstalling {item.BundleId}");

            UninstallOutcome outcome;
            try
            {
                outcome = await _cli.UninstallAsync(udid, item.BundleId, CliLog(udid), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return FailItem(udid, item, "Uninstall error: " + ex.Message);
            }

            if (outcome.TimedOut)
            {
                var after = await SafeProbeAsync(udid, ct).ConfigureAwait(false);
                if (!after.Present)
                    return PauseDevice(udid, item, "Uninstall timed out; device went offline", disconnect: true);
                return FailItem(udid, item, "Uninstall timed out: no output for 180 s; process terminated");
            }

            if (!outcome.LooksSuccessful)
            {
                var after = await SafeProbeAsync(udid, ct).ConfigureAwait(false);
                var reason = outcome.FirstError ?? ("Exit code " + outcome.ExitCode);
                if (!after.Present)
                    return PauseDevice(udid, item, "Device offline (uninstall interrupted): " + reason, disconnect: true);

                // 卸载报错但回读确认已消失 → 视为成功（不能只看退出码，总览 §6.3）
                var gone = await TryConfirmUninstalledAfterFailureAsync(udid, item, ct).ConfigureAwait(false);
                if (gone)
                    return CompleteItem(udid, item, $"Uninstall reported an error ({reason}), but readback confirmed the app is gone");

                return FailItem(udid, item, "Uninstall incomplete: " + reason);
            }
        }

        return null;
    }

    /// <summary>步骤 9–10：回读确认 + 完成收尾（返回该条目/设备是否要暂停）。</summary>
    private async Task<bool> VerifyAndCompleteAsync(string udid, QueueItem item, CancellationToken ct)
    {
        // 9 回读确认（B3）：成功判定第三条。未接入时必须明说，不留"假装验证过"。
        if (_readback is null)
        {
            LogDetail(udid, $"Readback not enabled; judged by exit code + ERROR lines only");
        }
        else
        {
            SetStatus(udid, item, QueueStatus.Readback);
            var (ok, note, unknown) = await VerifyByReadbackAsync(udid, item, ct).ConfigureAwait(false);
            if (ok) return CompleteItem(udid, item, note);

            if (unknown)
            {
                // 回读异常不能当"设备上没装"：先探测设备，离线走暂停（置 WasInterrupted），
                // 在线但 3 次回读仍异常：无法验证结果 → 判失败（2026-11-07 用户定）。
                var after = await SafeProbeAsync(udid, ct).ConfigureAwait(false);
                if (!after.Present)
                    return PauseDevice(udid, item, "Readback timed out or errored; device offline (result unknown)", disconnect: true);

                return FailItem(udid, item, note ?? "Could not verify result");
            }

            return FailItem(udid, item, note ?? "Readback confirmation failed");
        }

        // 10 完成（本次会话保留显示，完成即从队列删除在 M6 接 DB 时实现）
        return CompleteItem(udid, item, null);
    }

    private enum RecoveryOutcome
    {
        AlreadyInstalled,
        NotInstalled,
        Unknown,
    }

    /// <summary>
    /// 恢复条目（曾被中断）的执行前回读：已装且版本一致 → 直接记完成，不重装（07 §3.5）。
    /// 版本未知时只按 BundleId 判定已装，不推测版本。
    /// </summary>
    private async Task<RecoveryOutcome> TryRecoverAlreadyInstalledAsync(
        string udid, QueueItem item, CancellationToken ct)
    {
        IReadOnlyList<InstalledApp> installed = Array.Empty<InstalledApp>();
        for (var attempt = 1; attempt <= ReadbackAttempts; attempt++)
        {
            try
            {
                _readback!.Invalidate(udid);
                installed = await _readback.ListInstalledAsync(udid, ct).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogDetail(udid, $"Pre-resume readback attempt {attempt}/{ReadbackAttempts} incomplete: {ex.Message}");
                if (attempt >= ReadbackAttempts) return RecoveryOutcome.Unknown;
                await Task.Delay(ReadbackRetryDelay, ct).ConfigureAwait(false);
            }
        }

        RaiseInstalledApps(udid, installed);
        var hit = InstalledAppSet.Find(installed, item.BundleId);
        item.HadInstalledBefore ??= hit is not null;
        if (hit is null)
        {
            LogDetail(udid, $"Pre-resume readback: {item.BundleId} not on device; continuing install");
            return RecoveryOutcome.NotInstalled;
        }

        var match = InstalledAppSet.VersionsMatch(hit.Value, item.PackageVersion);
        switch (match)
        {
            case true:
                CompleteItem(udid, item, $"Previous run interrupted; readback confirmed {hit.Value.Version} installed; retry skipped");
                return RecoveryOutcome.AlreadyInstalled;
            case null:
                // 版本未知（ShortVersion / Build 都缺）：只按 BundleId 判定，不推测
                CompleteItem(udid, item, "Previous run interrupted; readback confirmed installed (version unknown); retry skipped");
                return RecoveryOutcome.AlreadyInstalled;
            default:
                LogDetail(udid, $"Pre-resume readback: installed {hit.Value.Version} ≠ package version {item.PackageVersion}; reinstalling");
                return RecoveryOutcome.NotInstalled;
        }
    }

    /// <summary>安装报错后再回读：装上了就返回版本说明串，没装上 / 回读失败返回 null。</summary>
    private async Task<string?> TryConfirmInstalledAfterFailureAsync(
        string udid, QueueItem item, CancellationToken ct)
    {
        try
        {
            _readback!.Invalidate(udid);
            var installed = await _readback.ListInstalledAsync(udid, ct).ConfigureAwait(false);
            RaiseInstalledApps(udid, installed);
            var hit = InstalledAppSet.Find(installed, item.BundleId);
            if (hit is null) return null;
            return hit.Value.VersionKnown ? $" (version {hit.Value.Version})" : " (version unknown)";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogDetail(udid, $"Readback retry failed: {ex.Message} (treated as failure)");
            return null;
        }
    }

    /// <summary>卸载报错后再回读：应用确实消失返回 true。</summary>
    private async Task<bool> TryConfirmUninstalledAfterFailureAsync(
        string udid, QueueItem item, CancellationToken ct)
    {
        try
        {
            _readback!.Invalidate(udid);
            var installed = await _readback.ListInstalledAsync(udid, ct).ConfigureAwait(false);
            RaiseInstalledApps(udid, installed);
            return InstalledAppSet.Find(installed, item.BundleId) is null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogDetail(udid, $"Readback retry failed: {ex.Message} (treated as failure)");
            return false;
        }
    }

    /// <summary>
    /// 回读确认（成功判定第三条）。最多 3 次、间隔 1 s；异常 / 没读到 / 版本不匹配都重试。
    /// 返回 ok + 可写进 Note 的说明；Unknown=true 表示"结果未知"（异常且设备在线/离线）。
    /// </summary>
    private async Task<(bool Ok, string? Note, bool Unknown)> VerifyByReadbackAsync(
        string udid, QueueItem item, CancellationToken ct)
    {
        string? lastFailure = null;
        for (var attempt = 1; attempt <= ReadbackAttempts; attempt++)
        {
            IReadOnlyList<InstalledApp> installed;
            try
            {
                _readback!.Invalidate(udid);
                installed = await _readback.ListInstalledAsync(udid, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogDetail(udid, $"Readback attempt {attempt}/{ReadbackAttempts} error: {ex.Message}");
                var probe = await SafeProbeAsync(udid, ct).ConfigureAwait(false);
                if (!probe.Present)
                    return (false, "Device offline; could not verify result", true);

                lastFailure = "Could not verify result";
                if (attempt < ReadbackAttempts)
                {
                    await Task.Delay(ReadbackRetryDelay, ct).ConfigureAwait(false);
                    continue;
                }

                return (false, lastFailure, true);
            }

            RaiseInstalledApps(udid, installed);
            var hit = InstalledAppSet.Find(installed, item.BundleId);

            if (item.Kind == JobKind.Install)
            {
                if (hit is null)
                {
                    lastFailure = $"Readback confirmation failed: {item.BundleId} is not installed on the device";
                    LogDetail(udid, $"Readback attempt {attempt}/{ReadbackAttempts}: {item.BundleId} not found");
                }
                else
                {
                    var match = InstalledAppSet.VersionsMatch(hit.Value, item.PackageVersion);
                    if (match == true)
                        return (true, $"Readback confirmed: {hit.Value.Version} installed", false);
                    if (match is null)
                        return (true, "Readback confirmed installed (version unknown)", false);

                    item.AutoRetrySuppressed = true;   // 立刻置位：后续轮次异常也绕不过
                    lastFailure = $"Device has {hit.Value.Version} installed, which does not match package version {item.PackageVersion}";
                    LogDetail(udid, $"Readback attempt {attempt}/{ReadbackAttempts}: version mismatch: {lastFailure}");
                }
            }
            else
            {
                if (hit is null)
                    return (true, "Readback confirmed uninstalled", false);

                lastFailure = $"Readback not confirmed: {item.BundleId} is still present (version {(hit.Value.VersionKnown ? hit.Value.Version : "unknown")})";
                LogDetail(udid, $"Readback attempt {attempt}/{ReadbackAttempts}: {item.BundleId} still present");
            }

            if (attempt < ReadbackAttempts)
                await Task.Delay(ReadbackRetryDelay, ct).ConfigureAwait(false);
        }

        // 已检测到 App 存在但版本不符：终判且禁止自动重装（覆盖安装会 CreatingContainer 清数据）。
        return (false, lastFailure ?? "Readback not confirmed", false);
    }

    /// <summary>返回非 null = 空间不足（记失败）；返回 null = 通过或按口径跳过。</summary>
    private async Task<string?> CheckSpaceAsync(string udid, QueueItem item, CancellationToken ct)
    {
        DiskUsage usage;
        try
        {
            usage = await _cli.QueryDiskUsageAsync(udid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { usage = DiskUsage.Unknown; }

        if (!usage.Ok)
        {
            var fallback = await SafeAfcSpaceAsync(udid, ct).ConfigureAwait(false);
            if (fallback.Ok)
                usage = new DiskUsage(true, fallback.TotalBytes, fallback.FreeBytes);
        }

        if (!usage.Ok)
        {
            LogDetail(udid, $"Could not read free space; skipping space check (install may still fail)");
            return null;
        }

        long compressed = 0;
        try { compressed = new FileInfo(item.FilePath!).Length; } catch { }

        if (compressed <= 0)
        {
            LogDetail(udid, $"Package size unknown; skipping space check");
            return null;
        }

        // 空间口径（2026-11-01 实测 + 用户拍板）：需要 = 峰值占用（压缩包 + 解压后）+ 10 MB 余量。
        // 实测（iPhone 5 / DOOM 87.5 MB）：安装段峰值下降 173.68 MB ≈ staging 87.57 + 已装 87.57，
        // 卸载归还后可用回到起点；用 zip 的 AppSize（91.58 MB）算峰值是安全上界。
        // 解压未知已在入队时按压缩 × 2 写进 PeakBytes（2026-11-07），投影与执行共用这一份。
        var expected = item.ExpectedUncompressedBytes is > 0 ? item.ExpectedUncompressedBytes.Value : (long?)null;
        var peak = item.PeakBytes > 0 ? item.PeakBytes : (expected is null ? compressed * 2 : compressed + expected.Value);
        var required = peak + SpaceMarginBytes;

        if (expected is null)
        {
            // 包没深解析成功（解析失败 / 刚添加还没解析）：按压缩 ×2 保守估算，并记一条警告
            LogDetail(udid, $"Uncompressed size unknown (deep parse failed); estimated conservatively as 2x compressed: {FormatBytes(peak)}");
        }
        else
        {
            LogDetail(udid, $"Space check: peak {FormatBytes(peak)} (compressed {FormatBytes(compressed)} + uncompressed {FormatBytes(expected.Value)})" +
                        $"+ margin {FormatBytes(SpaceMarginBytes)} = {FormatBytes(required)} required, {FormatBytes(usage.FreeBytes)} available");
        }

        if (usage.FreeBytes < required)
        {
            var detail = expected is null
                ? $"(uncompressed unknown, estimated at 2x compressed)"
                : $"(compressed {FormatBytes(compressed)} + uncompressed {FormatBytes(expected.Value)})";
            return $"Not enough space: {FormatBytes(usage.FreeBytes)} available, {FormatBytes(required)} required" + detail;
        }

        return null;
    }

    private async Task<AfcSpaceOutcome> SafeAfcSpaceAsync(string udid, CancellationToken ct)
    {
        try
        {
            return await _afc.QuerySpaceAsync(udid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new AfcSpaceOutcome(true, false, 0, 0, "AFC space query failed");
        }
    }

    private async Task<DeviceProbe> SafeProbeAsync(string udid, CancellationToken ct)
    {
        try
        {
            return await _cli.ProbeAsync(udid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return DeviceProbe.Missing;
        }
    }

    // ── 结果分支 ──────────────────────────────────────────────────────────────

    /// <summary>普通失败：记 Failed + Note，返回 false 让泵继续下一个包（07 §2 / §3.1）。</summary>
    /// <summary>回读结果未知（设备在线）：标"需确认"，不自动重装，也不覆盖 Completed/Failed 语义。</summary>
    /// <summary>
    /// 执行前检查源文件：返回 null = 可用；否则返回失败原因。
    /// 判据是"存在 + 可读"：被占用（IOException）等 200 ms 重试一次——杀软/索引偶发占用不该判死。
    /// 等待可取消（审核 P2-1：原来是不带取消的 Thread.Sleep）。
    /// </summary>
    private async Task<string?> CheckPackageFileAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No file path";
        if (!File.Exists(path)) return "Source file no longer exists (deleted or moved)";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                return "Source file unavailable (in use or no read permission): " + ex.Message;
            }
        }

        return null;
    }

    /// <summary>失败自动再入队：删旧行，同参数加入新行；返回 false = 无法入队（按终判失败）。</summary>
    private bool TryAutoRequeueFailed(string udid, QueueItem failed)
    {
        var attempt = failed.AutoRetryCount + 1;
        var retry = new QueueItem
        {
            Udid = failed.Udid,
            Kind = failed.Kind,
            BundleId = failed.BundleId,
            FilePath = failed.FilePath,
            PackageKey = failed.PackageKey,
            PackageVersion = failed.PackageVersion,
            PackageMinOs = failed.PackageMinOs,
            ExpectedUncompressedBytes = failed.ExpectedUncompressedBytes,
            PeakBytes = failed.PeakBytes,
            HadInstalledBefore = failed.HadInstalledBefore,
            Status = _suspended.ContainsKey(udid) ? QueueStatus.Suspended : QueueStatus.Pending,
            Note = _suspended.ContainsKey(udid) ? "Queue suspended" : $"Attempt {attempt}",
            AutoRetryCount = attempt,
            OrderIndex = 0,
        };

        if (!_queues.TryGetValue(udid, out var list)) return false;
        lock (GateFor(udid))
        {
            if (!list.Contains(failed)) return false;
            list.Remove(failed);
            retry.OrderIndex = NextOrderIndex(list);
            list.Add(retry);
            ReorderPending(list);
            Interlocked.Increment(ref _version);
        }

        LogEnd(udid, $"Auto-retry: {DisplayName(failed)}, attempt {attempt} (previous failed row removed)");
        RaiseJobRemoved(udid, failed.Id);
        RaiseJobAdded(udid, retry);
        return true;
    }

    private bool FailItem(string udid, QueueItem item, string note)
    {
        item.Note = note;
        SetStatus(udid, item, QueueStatus.Failed, note);
        LogError(udid, $"{DisplayName(item)} incomplete: {note} (continuing with next)");
        return false;
    }

    /// <summary>
    /// 空间不足：暂停设备队列，等用户腾出空间后点继续（2026-11-07 用户定）。
    /// 不标 WasInterrupted：尚未上传，不是中断恢复场景。
    /// </summary>
    private bool SuspendForSpace(string udid, QueueItem item, string note)
    {
        // 空间不足属于队列级挂起：所有等待任务转灰色挂起，等用户腾出空间后点继续。
        _suspended[udid] = 1;
        var list = _queues.GetOrAdd(udid, _ => new List<QueueItem>());
        lock (GateFor(udid))
        {
            foreach (var pending in list.Where(x => x.Status == QueueStatus.Pending))
            {
                pending.Status = QueueStatus.Suspended;
                pending.Note = "Queue suspended";
                RaiseStatus(udid, pending);
            }

            Interlocked.Increment(ref _version);
        }

        item.Note = note;
        SetStatus(udid, item, QueueStatus.Suspended, note);
        RaiseRunState(udid, QueueRunState.Suspended, note);
        LogEnd(udid, $"Suspended: {note}");
        return true;
    }

    /// <summary>设备级问题：标 Paused，返回 true 让泵停住等用户点继续。</summary>
    private bool PauseDevice(string udid, QueueItem item, string note, bool disconnect = false)
    {
        item.Note = note;
        // 标记"被中断过"：用户点继续后要先回读确认是否其实已经装好了（07 §3.5）
        item.WasInterrupted = true;
        SetStatus(udid, item, QueueStatus.Paused, note);
        if (disconnect) SuspendPendingForDisconnect(udid);
        RaiseRunState(udid, QueueRunState.Paused, note);
        LogEnd(udid, $"Paused: {note} (click Resume to retry)");
        return true;
    }

    /// <summary>成功收尾：状态 Done + 进度 100% + 可选的 Note（回读结论写在这里）。</summary>
    private bool CompleteItem(string udid, QueueItem item, string? note)
    {
        item.Percent = 100;
        RaiseProgress(udid, item, 100);
        item.Note = note;
        SetStatus(udid, item, QueueStatus.Done, note);
        LogEnd(udid, $"Completed {DisplayName(item)}" + (note is null ? "" : $"（{note}）"));
        return false;
    }

    private void SetStatus(string udid, QueueItem item, QueueStatus status, string? note = null)
    {
        Interlocked.Increment(ref _version);
        item.Status = status;
        if (note is not null) item.Note = note;
        RaiseStatus(udid, item);
    }

    private void RaiseStatus(string udid, QueueItem item)
        => _events.Report(new JobStatusEvent(udid, item.Id, item.Status, item.Note, item.BundleId, item.Kind));

    private void RaiseInstalledApps(string udid, IReadOnlyList<InstalledApp> apps)
        => _events.Report(new InstalledAppsEvent(udid, apps));

    private void RaiseJobAdded(string udid, QueueItem item)
        => _events.Report(new JobAddedEvent(udid, item));

    private void RaiseJobRemoved(string udid, Guid jobId)
        => _events.Report(new JobRemovedEvent(udid, jobId));

    private void RaiseProgress(string udid, QueueItem item, double percent)
    {
        item.Percent = percent;
        _events.Report(new JobProgressEvent(udid, item.Id, percent));
    }

    private void RaiseRunState(string udid, QueueRunState state, string? note)
        => _events.Report(new DeviceRunStateEvent(udid, state, note));

    private static string DisplayName(QueueItem item)
        => item.Kind == JobKind.Install
            ? Path.GetFileName(item.FilePath ?? item.BundleId)
            : item.BundleId;

    private static string Short(string udid) => udid[..Math.Min(8, udid.Length)];

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>进度节流：同一任务合并到 10 次/秒（01 §6.3）；阶段切换不走这里，立即上报。</summary>
internal sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly Action<T> _sink;
    private readonly long _intervalTicks;
    private long _lastTicks = -1;
    private readonly object _gate = new();

    public ThrottledProgress(Action<T> sink, int intervalMs = 100)
    {
        _sink = sink;
        _intervalTicks = intervalMs * TimeSpan.TicksPerMillisecond;
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow.Ticks;
            if (_lastTicks >= 0 && now - _lastTicks < _intervalTicks) return;
            _lastTicks = now;
        }
        _sink(value);
    }
}
