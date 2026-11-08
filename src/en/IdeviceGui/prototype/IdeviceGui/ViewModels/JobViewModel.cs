using System.IO;
using IdeviceGui.Services;

namespace IdeviceGui.ViewModels;

/// <summary>
/// 队列里的一条任务（界面侧的状态对象，01 §5 的 QueueItemState）。
/// 领域对象是 QueueService 里的 QueueItem，两者靠 <see cref="Id"/> 对应；
/// 状态变化由 QueueService 经 IProgress&lt;QueueEvent&gt; 投递到 UI 线程后改写本对象。
/// </summary>
internal sealed class JobViewModel : ObservableObject
{
    private QueueStatus _status = QueueStatus.Pending;
    private string? _note;
    private double _percent;
    private double _installPercent;
    private bool _installStarted;

    public JobViewModel(QueueItem item, string deviceName)
    {
        Id = item.Id;
        Kind = item.Kind;
        FilePath = item.FilePath;
        BundleId = item.BundleId;
        PeakBytes = item.PeakBytes;
        OrderIndex = item.OrderIndex;
        DeviceName = deviceName;
        _status = item.Status;
        _note = item.Note;
        RefreshVisual();
    }

    public Guid Id { get; }
    public JobKind Kind { get; private set; }
    public string? FilePath { get; private set; }
    public string BundleId { get; }

    /// <summary>峰值占用（压缩+解压）；仅显示/空间用，队列顺序改由服务端 OrderIndex 决定。</summary>
    public long PeakBytes { get; private set; }

    /// <summary>服务端队列顺序号；DeviceQueueViewModel 从 QueueService 同步后用于界面排序。</summary>
    public int OrderIndex { get; internal set; }

    /// <summary>
    /// 同一条队列表项被复用（2026-11-02：一个包只有一行）：入队/卸载把 Done 行原地改成新任务时调用，
    /// 不能新建行，否则队列里同一个包会出现两条。
    /// </summary>
    internal void UpdateFrom(QueueItem item)
    {
        Kind = item.Kind;
        FilePath = item.FilePath;
        PeakBytes = item.PeakBytes;
        OrderIndex = item.OrderIndex;
        Status = item.Status;
        Note = item.Note;
        Percent = item.Percent;
        InstallPercent = item.InstallPercent;
        InstallStarted = item.InstallPercent > 0;   // 重入队时由 QueueService 把 InstallPercent 归零
        RefreshVisual();
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Display));
    }
    public string DeviceName { get; }

    public string FileName => FilePath is null ? BundleId : Path.GetFileName(FilePath);

    public QueueStatus Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(DisplayPercentText));
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(CanRetry));
            RefreshVisual();
        }
    }

    private OperationVisual _visual;

    /// <summary>统一操作颜色状态；队列行 / Tab 块都从这里取。</summary>
    public OperationVisual Visual
    {
        get => _visual;
        private set
        {
            if (!SetProperty(ref _visual, value)) return;
            OnPropertyChanged(nameof(RowBrush));
        }
    }

    public System.Windows.Media.Brush RowBrush => _visual.ToBrush();

    private void RefreshVisual() => Visual = OperationVisuals.Resolve(Kind, _status);

    public string StatusText => _status.ToText();

    public string? Note
    {
        get => _note;
        set
        {
            if (!SetProperty(ref _note, value)) return;
            OnPropertyChanged(nameof(Display));
        }
    }

    public double Percent
    {
        get => _percent;
        set
        {
            if (!SetProperty(ref _percent, value)) return;
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>
    /// 安装段是否已开始（收到过第一条 `Install: <阶段> (NN%)`，**含 0%**）。
    /// 页签条用它判"进入安装段"（审查 N9：`InstallPercent &gt; 0` 会把 `Install: … (0%)` 误当成传输段）。
    /// </summary>
    public bool InstallStarted
    {
        get => _installStarted;
        private set => SetProperty(ref _installStarted, value);
    }

    /// <summary>标记"安装段已开始"（不受百分比是否为 0 影响）。</summary>
    internal void NoteInstallStarted() => InstallStarted = true;

    /// <summary>重入队 / 回到 Pending：清掉安装段标记与百分比。</summary>
    internal void ResetInstallProgress()
    {
        InstallStarted = false;
        InstallPercent = 0;
    }

    /// <summary>
    /// 安装段进度 0–100（2026-11-03 用户定）：来自 CLI 的 `Install: <阶段> (NN%)`；
    /// 队列行显示 `[安装中 NN%]`，页签条后 40% 也用它。
    /// </summary>
    public double InstallPercent
    {
        get => _installPercent;
        set
        {
            if (!SetProperty(ref _installPercent, value)) return;
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(DisplayPercentText));
        }
    }

    /// <summary>队列行百分比后缀：安装段用 `InstallPercent`，其余（传输等）用 `Percent`；0/100 不显示。</summary>
    public string DisplayPercentText => _status == QueueStatus.Installing && _installPercent > 0 && _installPercent < 100
        ? $" {_installPercent:0}%"
        : _percent > 0 && _percent < 100
            ? $" {_percent:0}%"
            : "";

    public bool CanRetry => _status.IsRetryable();

    /// <summary>
    /// 能否点「取消选中」（M4-e1）：Pending / 安装前阶段立刻取消；上传中杀进程；安装段 = 装完自动卸载。
    /// T1（2026-11-03）：判定统一走 <see cref="QueueStatusKinds.IsCancellable"/>——它带 `Kind`，
    /// 卸载任务的运行段本来就不能取消（旧手抄列表会误显示为可点）。
    /// </summary>
    public bool CanCancel => _status.IsCancellable(Kind);

    public string Display =>
        $"{FileName}   [{StatusText}{DisplayPercentText}]" +
        (string.IsNullOrEmpty(Note) ? "" : $"   {Note}");

    // UIA 名称与界面一致（AGENTS §6.3）
    public override string ToString() => Display;
}
