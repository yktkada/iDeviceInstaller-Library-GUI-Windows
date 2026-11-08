using System.Collections.ObjectModel;
using IdeviceGui.Services;

namespace IdeviceGui.ViewModels;

/// <summary>
/// 一台设备的队列界面状态（01 §5 的 DeviceRuntimeState 雏形）。
/// 泵循环在 <see cref="QueueService"/> 里，本类只做投影：事件到达后在 UI 线程原地改属性，
/// 不重建集合、不自己读进程（硬约束 #8）。
/// </summary>
internal sealed class DeviceQueueViewModel : ObservableObject
{
    private readonly QueueService _queue;
    private QueueRunState _runState = QueueRunState.Idle;
    private string? _stateNote;
    private double _currentPercent;
    private IReadOnlyList<InstalledApp>? _installedApps;

    public DeviceQueueViewModel(QueueService queue, DeviceInfo device)
    {
        _queue = queue;
        Device = device;
        // DeviceInfo 会被原地更新（刷新设备不复建对象），这里把它的变更转发到本对象的派生属性
        Device.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DeviceInfo.Name)
                or nameof(DeviceInfo.Version)
                or nameof(DeviceInfo.Product))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(Display));
                OnPropertyChanged(nameof(QueueHeader));
            }
        };
        SuspendCommand = new AsyncRelayCommand(SuspendAsync, () => _runState != QueueRunState.Suspended);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => _runState is QueueRunState.Paused or QueueRunState.Suspended or QueueRunState.Offline);
        ToggleSuspendCommand = new AsyncRelayCommand(ToggleSuspendAsync);
        CancelSelectedCommand = new AsyncRelayCommand(CancelSelectedAsync, () => SelectedJob?.CanCancel == true);
    }

    /// <summary>中栏列表里选中的任务（「取消选中」用；2026-11-01 用户要求：只有 Pending 能立刻取消）。</summary>
    private JobViewModel? _selectedJob;

    public JobViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value)) return;
            CancelSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>取消选中任务：只对 Pending 生效（上传中/安装中的取消走 QueueService 的另一条路径）。</summary>
    public AsyncRelayCommand CancelSelectedCommand { get; }

    private Task CancelSelectedAsync()
    {
        // 与队列行双击 / 资料库双击共用同一个服务接口 QueueService.Cancel（2026-11-01 用户要求复用，不重写）
        var job = SelectedJob;
        if (job is not null)
        {
            var outcome = _queue.Cancel(Device.Udid, job.Id);
            if (outcome is QueueService.CancelOutcome.ScheduledAfterInstall)
            {
                // 安装段不能中止：标记装完自动卸载（服务内部会记日志/改状态）
            }
        }

        CancelSelectedCommand.RaiseCanExecuteChanged();
        return Task.CompletedTask;
    }

    public DeviceInfo Device { get; }
    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    public AsyncRelayCommand SuspendCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }

    /// <summary>挂起 / 继续合并按钮（2026-11-08 用户定）：运行/空闲时挂起，暂停/已挂起/离线时继续。</summary>
    public AsyncRelayCommand ToggleSuspendCommand { get; }

    public string Header => Device.ShortName;
    public string Display => Device.Display;

    public QueueRunState RunState
    {
        get => _runState;
        private set
        {
            if (!SetProperty(ref _runState, value)) return;
            OnPropertyChanged(nameof(RunStateText));
            OnPropertyChanged(nameof(SuspendResumeText));
            OnPropertyChanged(nameof(QueueHeader));
            OnPropertyChanged(nameof(IsBusy));
            SuspendCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            ToggleSuspendCommand.RaiseCanExecuteChanged();
        }
    }

    public string RunStateText => _runState.ToText();

    public string SuspendResumeText => _runState is QueueRunState.Paused or QueueRunState.Suspended or QueueRunState.Offline
        ? "继续"
        : "挂起";

    public string? StateNote
    {
        get => _stateNote;
        private set
        {
            if (!SetProperty(ref _stateNote, value)) return;
            OnPropertyChanged(nameof(QueueHeader));
        }
    }

    public double CurrentPercent
    {
        get => _currentPercent;
        private set
        {
            if (!SetProperty(ref _currentPercent, value)) return;
            OnPropertyChanged(nameof(QueueHeader));
        }
    }

    public bool IsBusy => _runState == QueueRunState.Running;

    /// <summary>
    /// 该设备已安装 App 的集合快照（B3 回读结果）。M4 的"已安装"列与卸载入口读它；
    /// 为 null 表示还没回读过。
    /// </summary>
    public IReadOnlyList<InstalledApp>? InstalledApps
    {
        get => _installedApps;
        private set
        {
            if (ReferenceEquals(_installedApps, value)) return;
            _installedApps = value;
            OnPropertyChanged(nameof(InstalledApps));
            OnPropertyChanged(nameof(InstalledAppsText));
        }
    }

    public string InstalledAppsText => _installedApps is null
        ? "已安装：未回读"
        : $"已安装：{_installedApps.Count} 个 App";

    /// <summary>中栏标题：`设备名 · 状态 [· 当前任务 45%]`（07 §3.7）。</summary>
    public string QueueHeader
    {
        get
        {
            var text = $"{Header} · {RunStateText}";
            if (_runState == QueueRunState.Running && _currentPercent > 0 && _currentPercent < 100)
                text += $" · 当前任务 {_currentPercent:0}%";
            if (!string.IsNullOrEmpty(_stateNote))
                text += $" · {_stateNote}";
            return text;
        }
    }

    public override string ToString() => QueueHeader;

    /// <summary>把入队返回的领域对象包成界面对象并显示在队列里。</summary>
    /// <summary>
    /// 队列显示顺序（2026-11-02 用户定：**安装优先级越高越靠上**）：
    /// ① 正在执行（保持原序）→ ② 等待中 Pending（按 PeakBytes 降序，同体积按入队序）→ ③ 暂停/需确认 → ④ 已结束（保持原序）。
    /// 纯计算，便于单测。
    /// </summary>
    internal static int[] ComputeJobOrder(IReadOnlyList<JobViewModel> jobs)
    {
        // T1：分类只走权威谓词（新增状态由 IsFinished/IsInFlight 强制表态，不会再落到 `_ => 0` 静默归组）
        static int Rank(QueueStatus status)
        {
            if (status.IsInFlight()) return 0;                          // 正在执行：永远最上
            if (status == QueueStatus.Pending) return 1;                // 等待中：置顶（仅次于执行中）
            if (status == QueueStatus.Suspended) return 2;               // 挂起：灰色，排在等待之后
            if (status == QueueStatus.Paused || status == QueueStatus.NeedsConfirm) return 3;
            if (status.IsFinished()) return 4;

            throw new ArgumentOutOfRangeException(nameof(status), status, "排序权重没有覆盖该状态（T1）");
        }

        var order = new int[jobs.Count];
        for (var i = 0; i < jobs.Count; i++) order[i] = i;

        Array.Sort(order, (left, right) =>
        {
            var rank = Rank(jobs[left].Status).CompareTo(Rank(jobs[right].Status));
            if (rank != 0) return rank;

            // Pending 只用服务端 OrderIndex（卸载优先/体积排序都由 QueueService 负责）；
            // 其它状态保持原序，避免 UI 再维护一套排序规则。
            if (jobs[left].Status == QueueStatus.Pending && jobs[right].Status == QueueStatus.Pending)
            {
                var byOrder = jobs[left].OrderIndex.CompareTo(jobs[right].OrderIndex);
                if (byOrder != 0) return byOrder;
            }

            return left.CompareTo(right);
        });

        return order;
    }

    /// <summary>按优先级重排界面队列（事件/入队后调用；Move 会触发界面更新）。</summary>
    public void ReorderJobs()
    {
        // 先同步服务端 OrderIndex，再排序：界面顺序不再自行按体积/类型推演。
        var indexes = _queue.GetOrderIndexes(Device.Udid);
        foreach (var job in Jobs)
        {
            if (indexes.TryGetValue(job.Id, out var index)) job.OrderIndex = index;
        }

        var order = ComputeJobOrder(Jobs);
        for (var target = 0; target < order.Length; target++)
        {
            var item = Jobs[order[target]];
            var current = Jobs.IndexOf(item);
            if (current != target) Jobs.Move(current, target);
        }

        RecomputeProgress();
    }

    public void RemoveJob(Guid jobId)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null) return;
        Jobs.Remove(job);
        RecomputeProgress();
    }

    public JobViewModel AddJob(QueueItem item)
    {
        var existing = Jobs.FirstOrDefault(j => j.Id == item.Id);
        if (existing is not null)
        {
            // 同一行被复用（例如 Done 行改成卸载）：原地覆盖属性，不新增行
            existing.UpdateFrom(item);
            RecomputeProgress();
            return existing;
        }

        var vm = new JobViewModel(item, Device.ShortName);
        Jobs.Add(vm);
        RecomputeProgress();
        return vm;
    }

    /// <summary>
    /// 清空"已结束"的任务（2026-11-01 用户定；2026-11-02 起由「刷新」复用，`⟳` = 清已结束 + 清标记）：
    /// 只保留**排队中（Pending）**与**运行中**（校验/预检/清理/空间/安装/卸载/回读），
    /// 以及可恢复的 Paused / NeedsConfirm；其余（Done / Failed / Cancelled）全部移除。
    /// 返回被移除的条数（日志/状态栏用，避免调用方再算一遍）。
    /// </summary>
    /// <summary>该状态是否属于"已结束"（可被「刷新」移出队列）；判定复用服务端的 <see cref="QueueStatusKinds.IsFinished"/>。</summary>
    internal static bool IsFinishedStatus(QueueStatus status) => status.IsFinished();

    public int ClearFinished()
    {
        var removed = 0;
        foreach (var job in Jobs.Where(j => IsFinishedStatus(j.Status)).ToList())
        {
            Jobs.Remove(job);
            removed++;
        }

        RecomputeProgress();
        return removed;
    }

    // ── 事件应用（全部在 UI 线程） ─────────────────────────────────────────────

    public void Apply(JobStatusEvent e)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == e.JobId);
        if (job is null) return;
        job.Note = e.Note;
        job.Status = e.Status;
        if (e.Status == QueueStatus.Pending) job.ResetInstallProgress();   // 回到待执行：清安装段标记（审查 N9）
        if (e.Status == QueueStatus.Pending) job.Percent = 0;
        if (e.Status == QueueStatus.Done) CurrentPercent = 0;
        RecomputeProgress();
    }

    public void Apply(JobProgressEvent e)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == e.JobId);
        if (job is null) return;
        job.Percent = e.Percent;
        CurrentPercent = e.Percent;
        // 2026-11-03 用户报 bug：传输阶段页签条不涨（传输是 `Transfer: x%` 事件，之前这里不重算，
        // 只有状态变化/安装段事件才重算 → 页签停在 1% 细边，看着像"原始色"，到安装段才出现）。
        RecomputeProgress();
    }

    /// <summary>安装段进度（`Install: <阶段> (NN%)`）：只更新行文案 + 页签条后 40%。</summary>
    public void Apply(JobInstallProgressEvent e)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == e.JobId);
        if (job is null) return;
        job.NoteInstallStarted();          // 审查 N9：`Install: … (0%)` 也算进入安装段
        job.InstallPercent = e.Percent;
        RecomputeProgress();
    }

    public void Apply(DeviceRunStateEvent e)
    {
        RunState = e.State;
        StateNote = e.Note;
        if (e.State is QueueRunState.Idle or QueueRunState.Paused or QueueRunState.Suspended or QueueRunState.Offline)
            CurrentPercent = 0;
        if (e.State == QueueRunState.Offline) Device.Online = false;
        else if (e.State == QueueRunState.Running) Device.Online = true;
    }

    /// <summary>
    /// 设备重新出现在 `idevice_id` 里（2026-11-02 修 bug）：**把队列状态从 Offline 复位成 Idle**，
    /// 否则界面一直显示"设备离线"（插回后 tab 恢复了，但标题/继续按钮仍是离线态）。
    /// </summary>
    internal void MarkOnline()
    {
        Device.Online = true;
        if (RunState == QueueRunState.Offline)
        {
            RunState = QueueRunState.Idle;
            StateNote = null;
        }
    }

    /// <summary>设备从列表消失：整行置离线、保留最后数据（02 §2）。</summary>
    internal void MarkOffline()
    {
        Device.Online = false;
        Apply(new DeviceRunStateEvent(Device.Udid, QueueRunState.Offline, "设备离线"));
    }

    public void Apply(InstalledAppsEvent e) => InstalledApps = e.Apps;

    private Task SuspendAsync()
    {
        _queue.Suspend(Device.Udid);
        return Task.CompletedTask;
    }

    private Task ResumeAsync()
    {
        _queue.Resume(Device.Udid);
        return Task.CompletedTask;
    }

    private Task ToggleSuspendAsync()
    {
        if (_runState is QueueRunState.Paused or QueueRunState.Suspended or QueueRunState.Offline)
            _queue.Resume(Device.Udid);
        else
            _queue.Suspend(Device.Udid);
        return Task.CompletedTask;
    }

    // ── 设备页签进度叠加（2026-11-02 用户定；docs/07 §3.10）────────────────────

    /// <summary>进行中最少显示宽度（1%）：有任务在跑就有一条细边（2026-11-03）。</summary>
    private const double MinProgressFraction = 0.01d;

    /// <summary>传输段占条宽的比重（剩下 0.4 给安装段；2026-11-03 用户定）。</summary>
    private const double TransferWeight = 0.6d;

    /// <summary>进度叠加形态。</summary>
    private QueueProgressVisual _progressVisual = QueueProgressVisual.None;
    private double _progressFraction;
    private bool _progressSuppressed;

    public int ProgressTotal { get; private set; }
    public int ProgressDone { get; private set; }
    public int ProgressFailed { get; private set; }
    public int ProgressUnfinished { get; private set; }

    /// <summary>已完成 / 分母（0..1）；终态与"无任务"时不用于画条。</summary>
    public double ProgressFraction => _progressFraction;

    /// <summary>画条用的宽度分数：**终态一律满格**（全绿/全红），进行中按已完成比例。</summary>
    public double ProgressFillFraction => IsProgressTerminal ? 1d : _progressFraction;

    public QueueProgressVisual ProgressVisual => _progressVisual;

    /// <summary>终态（全部处理完，有失败或全成功）——只有这种状态在点页签 800 ms 后才允许隐藏。</summary>
    public bool IsProgressTerminal => _progressVisual is QueueProgressVisual.AllDone or QueueProgressVisual.AllFailed;

    /// <summary>是否画叠加（隐藏后不画；队列再变化会重新出现）。</summary>
    public bool ProgressVisible => _progressVisual != QueueProgressVisual.None && !_progressSuppressed;

    /// <summary>点页签停留 800 ms 后隐藏终态填充。</summary>
    public void SuppressProgress()
    {
        if (_progressSuppressed || !IsProgressTerminal) return;
        _progressSuppressed = true;
        NotifyProgress();
    }

    /// <summary>按当前 `Jobs` 重算进度（任何队列状态变化后调用；会解除隐藏）。</summary>
    public void RecomputeProgress()
    {
        var total = 0;
        var done = 0;
        var failed = 0;
        var unfinished = 0;
        var progressSum = 0d;

        foreach (var job in Jobs)
        {
            if (job.Kind != JobKind.Install) continue;   // 只算安装任务

            switch (job.Status)
            {
                case QueueStatus.Done:
                    total++; done++; progressSum += 1; break;
                case QueueStatus.Failed:
                    total++; failed++; break;            // 失败占分母、不贡献进度
                case QueueStatus.Cancelled:
                    break;                               // 已取消不计
                default:
                    total++; unfinished++;
                    progressSum += JobProgressFraction(job);
                    break;
            }
        }

        ProgressTotal = total;
        ProgressDone = done;
        ProgressFailed = failed;
        ProgressUnfinished = unfinished;

        _progressVisual = total == 0
            ? QueueProgressVisual.None
            : unfinished > 0
                ? (failed > 0 ? QueueProgressVisual.FailedInProgress : QueueProgressVisual.InProgress)
                : (failed > 0 ? QueueProgressVisual.AllFailed : QueueProgressVisual.AllDone);

        var fraction = total == 0 ? 0d : progressSum / total;
        // 有任务在跑就至少留一条细边（刚开始还没回百分比的那一瞬间也不至于"完全没有条"）
        if (unfinished > 0 && fraction < MinProgressFraction) fraction = MinProgressFraction;
        _progressFraction = Math.Clamp(fraction, 0d, 1d);
        _progressSuppressed = false;   // 队列一有变化就重新按规则显示
        RecomputeOperationBar();
        NotifyProgress();
    }

    private IReadOnlyList<TabProgressBlock> _tabBlocks = Array.Empty<TabProgressBlock>();
    private OperationVisual _currentVisual;

    /// <summary>Tab 进度条分块：严格按 Jobs 当前显示顺序，每个未取消任务一块。</summary>
    public IReadOnlyList<TabProgressBlock> TabBlocks
    {
        get => _tabBlocks;
        private set => SetProperty(ref _tabBlocks, value);
    }

    /// <summary>Tab 最左固定状态块：仅当前任务；无当前但有暂停/待确认 → 灰；否则无。</summary>
    public OperationVisual CurrentVisual => _currentVisual;

    public System.Windows.Media.Brush CurrentStatusBrush => _currentVisual.ToBrush();
    public bool CurrentStatusVisible => _currentVisual != OperationVisual.None;

    private void RecomputeOperationBar()
    {
        // 2026-11-07 用户定：固定色块已经表示「当前任务」，进度条不再重复画这一块；
        // TabBlocks 从固定块之后的下一个任务开始，块数 = 未取消任务数 - 1（有当前时）。
        JobViewModel? currentJob = null;
        foreach (var job in Jobs)
        {
            if (job.Status == QueueStatus.Cancelled) continue;
            if (job.Status.IsInFlight())
            {
                currentJob = job;
                break;
            }
        }

        if (currentJob is null)
        {
            foreach (var job in Jobs)
            {
                if (job.Status != QueueStatus.Cancelled && job.Visual == OperationVisual.Paused)
                {
                    currentJob = job;
                    break;
                }
            }
        }

        _currentVisual = currentJob?.Visual ?? OperationVisual.None;
        var blocks = new List<TabProgressBlock>(Jobs.Count);
        foreach (var job in Jobs)
        {
            if (job.Status == QueueStatus.Cancelled) continue;
            if (ReferenceEquals(job, currentJob)) continue;
            blocks.Add(new TabProgressBlock(job.Id, job.Visual));
        }

        TabBlocks = blocks;
        OnPropertyChanged(nameof(CurrentVisual));
        OnPropertyChanged(nameof(CurrentStatusBrush));
        OnPropertyChanged(nameof(CurrentStatusVisible));
    }

    /// <summary>
    /// 单个任务的条内进度（2026-11-03 用户定）：**传输 0–100% → 前 60%，安装 0–100% → 后 40%**。
    /// 传输百分比来自 `Transfer: x%`（`Percent`），安装百分比来自 `Install: <阶段> (NN%)`（`InstallPercent`）。
    /// </summary>
    private static double JobProgressFraction(JobViewModel job)
    {
        // 注意：**传输也在 `Installing` 状态里跑**（状态在调 CLI 前就置 Installing），所以不能用状态判段，
        // 只能用"是否已经收到过 `Install: … (NN%)`"（`InstallPercent > 0`）来判"安装段已开始"。
        if (job.InstallStarted)
            return TransferWeight + (1d - TransferWeight) * Math.Clamp(job.InstallPercent, 0d, 100d) / 100d;

        return TransferWeight * Math.Clamp(job.Percent, 0d, 100d) / 100d;
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(ProgressVisual));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(ProgressFillFraction));
        OnPropertyChanged(nameof(ProgressVisible));
        OnPropertyChanged(nameof(IsProgressTerminal));
        OnPropertyChanged(nameof(ProgressTotal));
        OnPropertyChanged(nameof(ProgressDone));
        OnPropertyChanged(nameof(ProgressFailed));
        OnPropertyChanged(nameof(ProgressUnfinished));
        OnPropertyChanged(nameof(ProgressText));
    }

    /// <summary>UIA/ToolTip 用：`已完成 x / 总 y`。</summary>
    public string ProgressText => ProgressTotal == 0 ? "" : $"已完成 {ProgressDone} / {ProgressTotal}";
}

/// <summary>设备 Tab 进度条的一块（一个未取消任务）。</summary>
internal sealed record TabProgressBlock(Guid Id, OperationVisual Visual)
{
    public System.Windows.Media.Brush Brush => Visual.ToBrush();
}

/// <summary>设备页签进度叠加的形态（docs/07 §3.10）。</summary>
internal enum QueueProgressVisual
{
    /// <summary>没有安装任务（或全被取消）→ 不画。</summary>
    None = 0,

    /// <summary>还在进行、没有失败：绿色进度条。</summary>
    InProgress = 1,

    /// <summary>还在进行、但已出现过失败：红色进度条（仍是进度条形态）。</summary>
    FailedInProgress = 2,

    /// <summary>全部处理完且无失败：整格深绿。</summary>
    AllDone = 3,

    /// <summary>全部处理完但有失败：整格红。</summary>
    AllFailed = 4,
}
