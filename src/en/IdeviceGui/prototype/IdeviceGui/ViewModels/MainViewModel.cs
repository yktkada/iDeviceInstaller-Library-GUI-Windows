using System.Threading;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using System.IO;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Groups;
using IdeviceGui.Services.Imaging;
using IdeviceGui.Services.Parsing;
using IdeviceGui.Services.Scanning;
using IdeviceGui.ViewModels.Library;

namespace IdeviceGui.ViewModels;

internal sealed class MainViewModel : ObservableObject
{
    private static readonly string IpaFolder = Path.Combine(AppContext.BaseDirectory, "library");
    private const int MaxLogLines = 2000;

    /// <summary>b3b：日志批量上屏间隔（原逐行 Add + 逐行 ScrollIntoView，扫描时把 UI 拖死）。</summary>
    private static readonly TimeSpan LogBatchInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>b3b：搜索输入防抖（设置键 Grid.SearchDebounceMs，默认 200 ms；可在设置窗口改）。</summary>
    private TimeSpan _searchDebounce = TimeSpan.FromMilliseconds(LibraryPrefs.DefaultSearchDebounceMs);

    /// <summary>b3b：显示行数不超过这个值就**不延迟**搜索（设置键 Grid.SearchDebounceRows，默认 1000）。</summary>
    private int _searchDebounceRowThreshold = LibraryPrefs.DefaultSearchDebounceRows;

    /// <summary>设备族文案：true=Retina，false=R（设置键 Grid.DeviceFamilyRetinaText，默认 true）。</summary>
    private bool _deviceFamilyRetinaText = true;

    /// <summary>列表图标显示尺寸（逻辑像素）。M4 定行高后改这里，并调 <see cref="IconCacheOptions.RegisterDisplaySize"/>。</summary>
    private const int IconDisplayPx = 32;

    /// <summary>实际使用的扫描根：命令行 <c>--root</c> 给了就用它，否则用程序目录下的 library。</summary>
    private readonly string _ipaFolder;

    private readonly ICliRunner _cli;
    private readonly ReadbackService _readback;
    private readonly QueueService _queue;
    private IReadOnlyList<QueueColumnSetting> _queueColumnSettings = QueueColumns.Default;
    private readonly IProgress<LogLine> _log;

    /// <summary>b3a：全量日志环（2000 行）。简洁模式下界面不显示的行也存在这里，切到详细立刻可见。</summary>
    private readonly List<LogLine> _allLines = new();

    /// <summary>b3a：日志面板模式（记忆在 Settings 的 <c>Log.PanelMode</c>，默认简洁）。</summary>
    private LogMode _logMode = LogMode.Simple;

    /// <summary>b3b：已判定要显示、还没上屏的日志（攒够 50 ms 或切模式时一次刷出）。</summary>
    private readonly List<LogLine> _pendingLogLines = new();

    private readonly IUiTimer? _uiTimer;
    private IDisposable? _logFlushTimer;
    private string? _pendingSearchText;
    private IDisposable? _searchTimer;
    private readonly IProgress<QueueEvent> _queueEvents;
    private readonly Func<string[]?> _pickIpaFiles;

    // B7 / M2：数据库、扫描器、文件日志（初始化失败不拖垮程序，只记 _dbError 并跳过扫描）
    private AppDatabase? _db;
    private IpaScanner? _scanner;

    /// <summary>扫描 / 图标参数（设置窗口应用后可原地替换，见 <c>CreateScanner</c>）。</summary>
    private ScanOptions? _scanOptions;
    private IconCacheOptions? _iconOptions;
    private IconRequest? _iconRequest;

    /// <summary>扫描根（多根，2026-11-02 用户定：可添加目录）——来自库表 ScanRoots。</summary>
    private readonly List<ScanRootRow> _scanRoots = new();

    /// <summary>扫描根显示编号（`RootN`）：规范化路径 → 编号（设置键 <c>Scan.RootNumbers</c>，2026-11-02）。</summary>
    private Dictionary<string, int> _rootNumbers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>根 Id → (路径, 显示名) 缓存：路径列显示 `RootN\子路径` 用。</summary>
    private readonly Dictionary<long, (string Path, string Label)> _rootDisplay = new();

    /// <summary>设置快照（2026-11-02 设置窗口）：设备轮询秒数 / 断连自动继续 / 图标 / 扫描批大小。</summary>
    private int _devicePollSeconds = 2;
    private bool _autoResume = true;
    private int _autoRetryAttempts = 2;
    private int _reconnectStableSeconds = 3;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reconnectTimers = new(StringComparer.Ordinal);
    private int _scanBatchSize = ScanOptions.DefaultBatchSize;
    private int _iconCacheMaxMb = IconCacheOptions.DefaultCacheMaxMb;
    private bool _iconMatchDisplayScale = true;
    private bool _iconDownscaleCache = true;

    /// <summary>「设置」窗口是否已打开（防重复开窗）。</summary>
    private bool _settingsWindowOpen;

    /// <summary>点设备页签停留 800 ms 后隐藏终态进度填充（docs/07 §3.10）。</summary>
    private DispatcherTimer? _progressHideTimer;
    private FileLog? _fileLog;
    private string? _dbError;

    /// <summary>文件日志是否开启（设置键 Log.ToFile；2026-11-02 起可在设置窗口改）。</summary>
    private bool _logToFile;

    /// <summary>
    /// 按当前设置（扫描批大小 + 图标缓存参数）重建扫描器（2026-11-02：设置窗口应用后调用）。
    /// 旧扫描器没有常驻后台任务，直接换引用即可。
    /// </summary>
    private void CreateScanner(AppDatabase db)
    {
        _scanOptions = new ScanOptions { BatchSize = Math.Max(1, _scanBatchSize) };
        _iconOptions = new IconCacheOptions
        {
            DownscaleCache = _iconDownscaleCache,
            CacheMaxMb = _iconCacheMaxMb,
        };
        _iconOptions.RegisterDisplaySize(IconDisplayPx);
        _iconRequest = new IconRequest(IconDisplayPx, DpiScale(), _iconMatchDisplayScale);

        _scanner = new IpaScanner(
            db,
            _scanOptions,
            message => _log.Report(LogLine.Detail(message)),
            AppPaths.DataDirectory,
            _iconOptions,
            _iconRequest,
            WpfImageDecoder.Decode,
            onRowsParsed: PublishParsedBatch);
    }


    /// <summary>程序生命周期取消源：扫描与设备刷新都挂它，关窗时能真正取消后台工作（体检报告 P1-5）。</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>M4-a：把解析批次切回 UI 上下文（Progress 在创建它的同步上下文回调）。</summary>
    private readonly IProgress<IReadOnlyList<PackageRow>> _batchProgress;

    /// <summary>M4-b2：图标后台解码完成后回 UI 赋值（`Freeze()` 过的位图）。</summary>
    private readonly IProgress<(LibraryRow Row, string? Key, object? Image)> _iconProgress;

    /// <summary>M4-b2：资料库视图逻辑（过滤 / 合并 / 排序，纯逻辑可单测）。</summary>
    private readonly LibraryView _libraryView = new();

    /// <summary>
    /// 绿色（安装成功）的数据源（2026-11-02 用户定）：只存内存、按设备分开记，**只有刷新能清且只清当前设备**。
    /// </summary>
    private readonly DeviceSuccessMarks _successMarks = new();
    private CancellationTokenSource? _indexPrefetchCts;
    private bool _rootDisplayDirty = true;
    private string? _lastMarkerSignature;
    private bool _lastMarkerUpdateInstalled;

    /// <summary>图标解码并发闸门（4 个；与扫描的解析并发分开，互不抢）。</summary>
    private readonly SemaphoreSlim _iconGate = new(4, 4);

    /// <summary>已排队、还没回来的图标加载（按行 Id 去重，避免重复解码同一张）。</summary>
    private readonly ConcurrentDictionary<long, byte> _pendingIcons = new();

    /// <summary>正在补"slow information"（容量 / 电池）的设备 UDID（去重，同一台同时只跑一份）。</summary>
    private readonly ConcurrentDictionary<string, byte> _detailRefreshing = new();

    /// <summary>
    /// 图标"negative cache"（进度 §5.2 第 8 条）：缺失 / 解码失败的 key 记下来，同一 key 不再重试；
    /// 扫描结束（`SyncLibrary`）清空——那时缓存文件可能刚被写出来。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _missingIcons = new(StringComparer.Ordinal);

    private LibraryRow? _selectedRow;
    private readonly Stopwatch _libraryUiClock = Stopwatch.StartNew();
    private long _lastLibraryUiRefreshMs;
    private IReadOnlyList<LibraryRow> _libraryRows = Array.Empty<LibraryRow>();

    // 多选模式（2026-11-07）：仅本次运行；选中状态在 LibraryRow.IsMultiSelected 上。
    private bool _isMultiSelectMode;
    private IReadOnlyList<LibraryRow> _visibleFilteredRows = Array.Empty<LibraryRow>();
    private HashSet<LibraryRow> _dragBaseSelection = new();

    // 用户分组（N1，2026-11-08）：groups.json 外挂；分组选择仅本次运行。
    private GroupFile _groupFile = new();
    private Dictionary<string, HashSet<long>> _groupRowIds = new(StringComparer.OrdinalIgnoreCase);
    private string _activeGroupName = "All";
    private bool _rebuildingGroupOptions;

    // 默认排序 / 默认搜索范围（只在设置窗口保存；点列头/改搜索范围只改当前视图，不写这两个默认值）
    private LibrarySort _defaultSort = new(LibrarySortField.AppSize, true);
    private SearchScope _defaultScope = new();

    /// <summary>三色/✓ 标记重算合并计时器（审查 P2-3：原先每个进度事件都重算全表，10k 库不可接受）。</summary>
    private DispatcherTimer? _markersTimer;

    /// <summary>设备在线轮询（2026-11-02 用户定）：2 s 只看 idevice_id（~12 ms），集合变化才做一次完整刷新。</summary>
    private DispatcherTimer? _devicePollTimer;
    private int _devicePolling;

    /// <summary>设备刷新互斥（1 = 有一轮在跑）：按钮刷新与轮询刷新不能并发改 `Devices`（踩坑 C35 追加）。</summary>
    private int _deviceRefreshing;

    /// <summary>刷新被互斥丢弃时置 1：本轮结束后补跑一次（审查 A5：丢弃不能没反馈/不补）。</summary>
    private int _deviceRefreshPending;

    /// <summary>连续"Device is in the list but detection failed"的次数：用于把轮询间隔退避，避免每 2 s 全量刷新 + 刷屏（审查 A5）。</summary>
    private int _deviceProbeFailStreak;

    // 设置写入队列（审查 N7）：SaveSetting 只入队，落库走一次事务；设置窗口「应用」会 await FlushSettingsAsync
    private readonly object _settingsWriteGate = new();
    private readonly List<KeyValuePair<string, string>> _pendingSettingsWrites = new();
    private Task _settingsWriteTask = Task.CompletedTask;

    /// <summary>资料库行高（2026-11-01 用户定：设置键 Grid.RowHeight，默认 32，夹紧 16–48）。</summary>
    private double _libraryRowHeight = 32;
    private string _libraryCountText = "";
    // 「添加 IPA」临时行的 Id：从 -1_000_000_000 递减。
    // 组行 Id 从 -1 递减（LibraryView._groupRowSeq），两段**不重叠**，按 Id 找回选中时才不会串（审核 P3-2）
    private long _nextSyntheticId = -1_000_000_000;

    /// <summary>「全量重扫」的下一次扫描口径：UI 线程置位、扫描启动时读走并复位（不跨调用残留）。</summary>
    private volatile bool _forceReparseForNextScan;

    // M4-a：分批上屏统计
    private int _parsedBatchCount;
    private int _parsedBatchRows;

    // 手动快速刷新互斥（1 = 正在扫）：连点只跑一轮
    private int _manualScanRunning;

    // 扫描期间资料库视图禁用 + 占位（2026-11-08 用户定）：扫描批次只落库，不再逐批 Build。
    private bool _isScanningLibrary;
    private string _scanProgressText = "Scanning library…";
    private bool _applyingScanResults;
    private bool _libraryRefreshPending;

    private string? _statusMessage;
    private bool _statusIsError;

    /// <summary>状态栏文字（最近一条提示 / 错误）。</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>扫描期间资料库视图禁用（网格 / 搜索 / 多选 / 合并 / 高级筛选 / 分组 / 扫描按钮），显示占位进度。</summary>
    public bool IsScanningLibrary
    {
        get => _isScanningLibrary;
        private set
        {
            if (!SetProperty(ref _isScanningLibrary, value)) return;
            OnPropertyChanged(nameof(IsLibraryInteractive));
            OnPropertyChanged(nameof(IsLibraryScanOverlayVisible));
        }
    }

    /// <summary>资料库视图当前是否可交互（扫描中为 false）。</summary>
    public bool IsLibraryInteractive => !_isScanningLibrary;

    /// <summary>扫描占位只盖资料库；设备应用视图不受影响（2026-11-08 用户报 bug）。</summary>
    public bool IsLibraryScanOverlayVisible => _isScanningLibrary && !_isDeviceAppsMode;

    /// <summary>扫描占位文字：「正在扫描资料库…（解析 123 / 50000）」。</summary>
    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>当前正在跑的扫描任务（启动扫描 / 手动刷新都登记，关窗要等它）。</summary>
    private Task? _activeScanTask;

    /// <summary>关窗时扫描没在宽限内收尾：此时不要 Dispose 库，交给进程退出（审核 P1-4）。</summary>
    private bool _scanShutdownIncomplete;

    private DeviceQueueViewModel? _selectedDevice;
    private bool _isDeviceAppsMode;
    private bool _isDeviceAppsMultiSelectMode;
    private HashSet<DeviceAppRow> _deviceAppsDragBaseSelection = new();
    private readonly ConcurrentDictionary<string, byte> _deviceAppsPrefetching = new();
    private CancellationTokenSource? _deviceAppsCts;
    private string _deviceAppsSignature = "";
    private readonly ObservableCollection<DeviceAppRow> _deviceApps = new();
    private bool _initialized;
    private volatile bool _shuttingDown;

    public MainViewModel(ICliRunner cli, IAfcHelper afc, Func<string[]?> pickIpaFiles,
        string? scanRootOverride = null, IUiTimer? uiTimer = null)
    {
        _cli = cli;
        _uiTimer = uiTimer;   // null = 立即上屏（单测默认；生产由 MainWindow 注入 DispatcherUiTimer）
        _pickIpaFiles = pickIpaFiles;
        _ipaFolder = string.IsNullOrWhiteSpace(scanRootOverride) ? IpaFolder : scanRootOverride;

        // Progress<T> 在创建线程（UI）捕获同步上下文：后台 Report 异步投递，不阻塞调用方（硬约束 #2）
        _log = new Progress<LogLine>(AppendLog);
        _queueEvents = new Progress<QueueEvent>(OnQueueEvent);
        _batchProgress = new Progress<IReadOnlyList<PackageRow>>(ApplyBatchRows);   // M4-a：分批上屏
        _iconProgress = new Progress<(LibraryRow Row, string? Key, object? Image)>(ApplyIcon);   // M4-b2：图标解码完回 UI

        // B3：回读（ideviceinstaller list --xml）作为成功判定第三条，并提供已安装集合
        _readback = new ReadbackService(cli);
        _queue = new QueueService(cli, afc, _readback, _queueEvents, _log);

        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        AddIpaCommand = new AsyncRelayCommand(AddIpaAsync);
        EnqueueCommand = new AsyncRelayCommand(EnqueueAsync,
            () => SelectedDevice is not null && SelectedLibraryRow is { IsGroup: false });
        ToggleLogModeCommand = new AsyncRelayCommand(ToggleLogModeAsync);   // b3a：日志面板 简洁 ⇄ 详细
        // 命令内部异常：状态红字 + 日志（用户 2026-10-30 拍板）。
        // 注意别单独再订阅一次——下面这个循环已经包含全部命令（审核 P1-2 双重订阅）。

        foreach (var command in new AsyncRelayCommand[]
                 {
                     RefreshDevicesCommand, AddIpaCommand, EnqueueCommand, ToggleLogModeCommand,
                 })
        {
            command.Failed += ReportError;
        }

        RescanCommand = new AsyncRelayCommand(ManualScanAsync,
            () => !_shuttingDown && _activeScanTask is not { IsCompleted: false });   // 手动快速刷新（扫描中禁用，P2-6）
        RescanCommand.Failed += ReportError;

        // 全量重扫（2026-10-31 用户要）：连已成功的行也重新解析，补旧版本入库后为空的深字段
        RescanAllCommand = new AsyncRelayCommand(ManualRescanAllAsync,
            () => !_shuttingDown && _activeScanTask is not { IsCompleted: false });
        RescanAllCommand.Failed += ReportError;

        // 全局未处理异常（App 级）也进状态栏 + 日志。事件可能来自任意线程（AppDomain / 线程池），
        // 所以 ReportError 内部会先切回 UI 线程；用方法引用是为了关窗时能退订（审核 P1-2）。
        App.UnhandledErrorReported += OnUnhandledError;
    }

    public ObservableCollection<DeviceQueueViewModel> Devices { get; } = new();

    /// <summary>
    /// 资料库网格的行序列（组行 + 文件行，M4-b2）：**整份替换**而不是逐行增删（审核 P1-2）。
    /// 行对象本身按 `Packages.Id` 复用（<see cref="Library.LibraryView"/> 保证），这里只换"View list"：
    /// 一次 PropertyChanged 代替 n 次 CollectionChanged，也避免"Selection cleared during rebuild"（界面绑 SelectedItem）。
    /// </summary>
    public IReadOnlyList<LibraryRow> LibraryRows
    {
        get => _libraryRows;
        private set => SetProperty(ref _libraryRows, value);
    }

    /// <summary>左侧当前视图：false = 资料库，true = 当前设备的用户 App（系统 App 不展示）。</summary>
    public bool IsDeviceAppsMode
    {
        get => _isDeviceAppsMode;
        set
        {
            if (!SetProperty(ref _isDeviceAppsMode, value)) return;
            OnPropertyChanged(nameof(IsLibraryScanOverlayVisible));
            if (value) _ = SafeFireAndForget.Run(RefreshDeviceAppsAsync, "Refresh device app");
            else
            {
                _deviceAppsCts?.Cancel();
                ExitDeviceAppsMultiSelect();
            }
        }
    }

    public ObservableCollection<DeviceAppRow> DeviceApps => _deviceApps;

    public IReadOnlyList<QueueColumnSetting> QueueColumnSettings => _queueColumnSettings;

    private void ApplyQueueColumnsToDevices()
    {
        var visible = QueueColumns.OrderedVisible(_queueColumnSettings);
        foreach (var device in Devices) device.ApplyQueueColumns(visible);
        OnPropertyChanged(nameof(QueueColumnSettings));
    }

    private string _deviceAppsCountText = "Device apps: not loaded";

    public string DeviceAppsCountText
    {
        get => _deviceAppsCountText;
        private set => SetProperty(ref _deviceAppsCountText, value);
    }

    /// <summary>设备应用页的独立多选（与资料库多选互不影响）。</summary>
    public bool IsDeviceAppsMultiSelectMode
    {
        get => _isDeviceAppsMultiSelectMode;
        private set
        {
            if (!SetProperty(ref _isDeviceAppsMultiSelectMode, value)) return;
            OnPropertyChanged(nameof(DeviceAppsMultiSelectButtonText));
            OnPropertyChanged(nameof(DeviceAppsSelectedCount));
            OnPropertyChanged(nameof(DeviceAppsBatchUninstallText));
            OnPropertyChanged(nameof(HasSelectedDeviceApps));
        }
    }

    public int DeviceAppsSelectedCount => DeviceApps.Count(r => r.IsMultiSelected);

    public string DeviceAppsMultiSelectButtonText =>
        IsDeviceAppsMultiSelectMode ? $"Multiple selection: {DeviceAppsSelectedCount} items" : "Multi-select: off";

    public string DeviceAppsBatchUninstallText => $"Uninstall selected ({DeviceAppsSelectedCount})";

    public bool HasSelectedDeviceApps => DeviceAppsSelectedCount > 0;

    private void NotifyDeviceAppsSelectionChanged()
    {
        OnPropertyChanged(nameof(DeviceAppsSelectedCount));
        OnPropertyChanged(nameof(DeviceAppsMultiSelectButtonText));
        OnPropertyChanged(nameof(DeviceAppsBatchUninstallText));
        OnPropertyChanged(nameof(HasSelectedDeviceApps));
    }

    /// <summary>资料库行高（DIP）；写入即持久化到 <c>Grid.RowHeight</c>（后台写，不阻塞 UI）。</summary>
    public double LibraryRowHeight
    {
        get => _libraryRowHeight;
        set
        {
            var clamped = Math.Clamp(value, LibraryPrefs.MinRowHeight, LibraryPrefs.MaxRowHeight);
            if (!SetProperty(ref _libraryRowHeight, clamped)) return;
            if (_db is null) return;

            // 审查 N2/N9：设置写入不能在 UI 线程同步做（库被外部锁住最长等 busy_timeout）；
            // 统一用后台 Task + **Task 内部** try/catch（外层 try 抓不到 Task 内异常）。
            var db = _db;
            var text = LibraryPrefs.FormatRowHeight(clamped);
            _ = SafeFireAndForget.Run(() => Task.Run(() =>
            {
                try { SettingsStore.SetString(db, LibraryPrefs.RowHeightSettingKey, text); }
                catch (Exception) { /* 只丢持久化，不影响界面 */ }
            }), "Save row height");
        }
    }

    private readonly SearchScope _searchScope = new();
    private SearchMatchMode _searchMatchMode = SearchMatchMode.Contains;

    /// <summary>搜索范围（多选列字段；2026-11-01 用户定：全部列都能搜）。</summary>
    public SearchScope SearchScope => _searchScope;

    /// <summary>兼容性下拉框（2026-11-05）：关 / 可安装 / 已适配；选择持久化。</summary>
    private CompatFilterMode _compatFilterMode = CompatFilterMode.Off;
    private AdvancedFilterState _advancedFilter = new();

    public sealed record CompatFilterOption(CompatFilterMode Mode, string Label);

    public IReadOnlyList<CompatFilterOption> CompatFilterOptions { get; } = new[]
    {
        new CompatFilterOption(CompatFilterMode.Off, "Compatibility filter: Off"),
        new CompatFilterOption(CompatFilterMode.Installable, "Compatible with this device"),
        new CompatFilterOption(CompatFilterMode.Adapted, "Adapted to this device"),
    };

    public CompatFilterMode CompatFilterMode
    {
        get => _compatFilterMode;
        set
        {
            if (!SetProperty(ref _compatFilterMode, value)) return;
            _libraryView.CompatFilter = value;
            RefreshLibraryRows(force: true);
            if (_db is not null) SaveSetting("Grid.CompatMode", FormatCompatMode(value));
            ReportStatus(value switch
            {
                CompatFilterMode.Installable => "Show packages: installable on current device",
                CompatFilterMode.Adapted => "Show packages: adapted to current device screen resolution",
                _ => "Show all",
            });
        }
    }

    private static string FormatCompatMode(CompatFilterMode mode) => mode switch
    {
        CompatFilterMode.Installable => "installable",
        CompatFilterMode.Adapted => "adapted",
        _ => "off",
    };

    private static CompatFilterMode ParseCompatMode(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "installable" => CompatFilterMode.Installable,
        "adapted" => CompatFilterMode.Adapted,
        _ => CompatFilterMode.Off,
    };

    /// <summary>把当前设备的机型/系统版本喂给资料库（兼容过滤用）。</summary>
    private string? _lastMismatchProduct;

    private void SyncLibraryDeviceContext()
    {
        var device = SelectedDevice?.Device;
        _libraryView.DeviceProductType = device?.Product;
        _libraryView.DeviceVersion = device?.Version;
        _libraryView.DeviceHardwareModel = device?.HardwareModel;

        var (_, mismatch) = DeviceScreen.Resolve(device?.HardwareModel, device?.Product);
        if (mismatch && !string.Equals(_lastMismatchProduct, device?.Product, StringComparison.Ordinal))
        {
            _lastMismatchProduct = device?.Product;
            ReportStatus($"Device model evidence conflict ({device?.Product}); using HardwareModel instead");
            AppendLog($"Device model evidence conflict ({device?.Product}); using HardwareModel instead", LogKind.Error);
        }
    }

    // ── 高级筛选（2026-11-05） ────────────────────────────────────────────
    public bool AdvancedFilterEnabled
    {
        get => _advancedFilter.Enabled;
        set
        {
            if (_advancedFilter.Enabled == value) return;
            _advancedFilter.Enabled = value;
            _advancedFilter.Touch();
            _libraryView.AdvancedFilter = _advancedFilter;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AdvancedFilterButtonText));
            RefreshLibraryRows(force: true);
            SaveAdvancedFilter();
        }
    }

    public string AdvancedFilterButtonText =>
        $"Advanced filter ({(_advancedFilter.Enabled ? _advancedFilter.ConditionCount : 0)})";

    internal AdvancedFilterState CaptureAdvancedFilter() => _advancedFilter.Clone();

    internal void ApplyAdvancedFilter(AdvancedFilterState state)
    {
        _advancedFilter = state;
        _advancedFilter.Enabled = state.Enabled;
        _advancedFilter.Touch();
        _libraryView.AdvancedFilter = _advancedFilter;
        OnPropertyChanged(nameof(AdvancedFilterEnabled));
        OnPropertyChanged(nameof(AdvancedFilterButtonText));
        RefreshLibraryRows(force: true);
        SaveAdvancedFilter();
    }

    private void SaveAdvancedFilter() =>
        SaveSetting("Grid.AdvancedFilter", AdvancedFilterCodec.Serialize(_advancedFilter));

    /// <summary>匹配方式：包含 / 前缀 / 精确。</summary>
    public SearchMatchMode SearchMatchMode => _searchMatchMode;

    /// <summary>计数文案（"N packages/M groups"）：重建时算一次，不再每来一行重算全表（审核 P3-7）。</summary>
    public string LibraryCountText
    {
        get => _libraryCountText;
        private set => SetProperty(ref _libraryCountText, value);
    }

    /// <summary>b3b：界面日志行（结构化对象；XAML 用 ToString + IsError/IsEnd 渲染）。</summary>
    public ObservableCollection<LogLine> LogLines { get; } = new();

    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public AsyncRelayCommand AddIpaCommand { get; }
    public AsyncRelayCommand EnqueueCommand { get; }

    /// <summary>b3a：日志面板模式切换（2026-10-31 用户拍板；选择写进 Settings 记住）。</summary>
    public AsyncRelayCommand ToggleLogModeCommand { get; }

    /// <summary>按钮文字随模式变：简洁 → 「切到详细」，详细 → 「切到简洁」。</summary>
    public string LogModeButtonText => _logMode == LogMode.Simple ? "Switch to detailed view" : "Switch to compact view";

    /// <summary>手动快速刷新：只解析新增/变化的包，没变的复用库里的结果。</summary>
    public AsyncRelayCommand RescanCommand { get; }

    /// <summary>全量重扫：所有在位文件都重新解析一遍（含已成功的行），补深字段 / 图标。</summary>
    public AsyncRelayCommand RescanAllCommand { get; }

    public DeviceQueueViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            SyncLibraryDeviceContext();
            if (_compatFilterMode != CompatFilterMode.Off) RefreshLibraryRows(force: true);
            if (_isDeviceAppsMode)
            {
                ExitDeviceAppsMultiSelect();
                _ = SafeFireAndForget.Run(RefreshDeviceAppsAsync, "Refresh device app");
            }
            EnqueueCommand.RaiseCanExecuteChanged();
            RefreshLibraryMarkers();
            StartProgressHideCountdown(value);
        }
    }

    /// <summary>
    /// 点设备页签（2026-11-02 用户定）：点**当前已选中**的页签不会触发 <see cref="SelectedDevice"/> 变化，
    /// 但"Stay for 800 ms to hide final state progress"仍需生效 → 由 code-behind 在页签 MouseDown 时调这里。
    /// </summary>
    public void NoteDeviceTabClicked(DeviceQueueViewModel device)
    {
        if (ReferenceEquals(SelectedDevice, device)) StartProgressHideCountdown(device);
    }

    /// <summary>
    /// 点设备页签**停留 800 ms** 后隐藏"Final state full grid filling"（2026-11-02 用户定，docs/07 §3.10）。
    /// 只有终态（全绿/全红）才隐藏；队列再变化时 <see cref="DeviceQueueViewModel.RecomputeProgress"/> 会解除隐藏。
    /// </summary>
    private void StartProgressHideCountdown(DeviceQueueViewModel? device)
    {
        _progressHideTimer?.Stop();
        _progressHideTimer = null;
        if (device is null) return;

        var target = device;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_progressHideTimer, timer)) return;
            _progressHideTimer = null;
            if (ReferenceEquals(SelectedDevice, target) && target.IsProgressTerminal) target.SuppressProgress();
        };
        _progressHideTimer = timer;
        timer.Start();
    }

    /// <summary>网格里选中的行（组行选中只用于展开/收起，不能入队）。</summary>
    public LibraryRow? SelectedLibraryRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            EnqueueCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>安装到…窗口的刷新按钮：等待一次完整设备刷新。</summary>
    public Task RefreshDevicesForInstallDialogAsync() => RefreshDevicesAsync();

    /// <summary>多选模式（2026-11-07）：仅本次运行，不写 Settings。</summary>
    public bool IsMultiSelectMode
    {
        get => _isMultiSelectMode;
        private set
        {
            if (!SetProperty(ref _isMultiSelectMode, value)) return;
            OnPropertyChanged(nameof(MultiSelectButtonText));
            OnPropertyChanged(nameof(MultiSelectedCount));
        }
    }

    public int MultiSelectedCount =>
        _libraryView.AllFiles.Count(r => !r.IsGroup && r.IsMultiSelected);

    public string MultiSelectButtonText =>
        IsMultiSelectMode ? $"Multi-select: {MultiSelectedCount} selected" : "Multi-select: off";

    private void NotifyMultiSelectionChanged()
    {
        OnPropertyChanged(nameof(MultiSelectedCount));
        OnPropertyChanged(nameof(MultiSelectButtonText));
    }

    public void ToggleMultiSelectMode()
    {
        IsMultiSelectMode = !IsMultiSelectMode;
        if (!IsMultiSelectMode) ClearMultiSelection(rebuild: false);
        RefreshLibraryRows(force: true);
        NotifyMultiSelectionChanged();
    }

    /// <summary>多选模式下单击文件行：切换选中（组行 / 非多选模式返回 false）。</summary>
    public bool ToggleLibraryMultiSelect(LibraryRow row)
    {
        if (!IsMultiSelectMode || row.IsGroup) return false;

        // 可见行只改属性让 DataTrigger 染色；不要为一次单击重建 50k 行。
        // 被筛选隐藏后追加到末尾的行：取消选中后必须重建，把它移出列表。
        var visibleInFilter = _visibleFilteredRows.Contains(row);
        row.IsMultiSelected = !row.IsMultiSelected;
        NotifyMultiSelectionChanged();
        if (!row.IsMultiSelected && !visibleInFilter)
            RefreshLibraryRows(force: true);
        return true;
    }

    /// <summary>
    /// 多选拖动开始：记录拖拽前基线；拖动范围按“每行切换”计算，而不是只选中。
    /// </summary>
    public bool BeginDragSelection(LibraryRow anchor)
    {
        if (!IsMultiSelectMode || anchor.IsGroup || anchor.IsAppendedSeparator) return false;
        if (!_visibleFilteredRows.Contains(anchor)) return false;
        _dragBaseSelection = _libraryView.AllFiles
            .Where(r => !r.IsGroup && r.IsMultiSelected)
            .ToHashSet();
        return true;
    }

    /// <summary>拖动范围：anchor 与 current 之间的行相对拖拽前状态切换（反选）；组行跳过。</summary>
    public void SelectDragRange(LibraryRow anchor, LibraryRow current)
    {
        if (!IsMultiSelectMode) return;
        if (!MultiSelectRange.Apply(
                _visibleFilteredRows,
                anchor,
                current,
                _dragBaseSelection,
                row => row.IsGroup || row.IsAppendedSeparator))
            return;
        NotifyMultiSelectionChanged();
    }

    /// <summary>设备应用拖动开始：与资料库同一套范围选择契约。</summary>
    public bool BeginDeviceAppDragSelection(DeviceAppRow anchor)
    {
        if (!IsDeviceAppsMultiSelectMode || !DeviceApps.Contains(anchor)) return false;
        _deviceAppsDragBaseSelection = DeviceApps.Where(r => r.IsMultiSelected).ToHashSet();
        return true;
    }

    /// <summary>设备应用拖动范围：复用 <see cref="MultiSelectRange"/>，不另写一套反选逻辑。</summary>
    public void SelectDeviceAppDragRange(DeviceAppRow anchor, DeviceAppRow current)
    {
        if (!IsDeviceAppsMultiSelectMode) return;
        if (!MultiSelectRange.Apply(
                DeviceApps,
                anchor,
                current,
                _deviceAppsDragBaseSelection,
                static _ => false))
            return;
        NotifyDeviceAppsSelectionChanged();
    }

    /// <summary>全选：只选当前筛选后可见的文件行，不含被筛选隐藏后追加到末尾的行。</summary>
    public void SelectAllVisibleLibraryRows()
    {
        if (!IsMultiSelectMode) return;
        var visible = _visibleFilteredRows.Where(r => !r.IsGroup).ToList();
        foreach (var row in visible) row.IsMultiSelected = true;
        NotifyMultiSelectionChanged();

        var hiddenMatching = _libraryView.FilteredFiles.Count(r => !r.IsGroup && !r.IsMultiSelected);
        if (hiddenMatching > 0)
        {
            AppendLog($"Select all: {visible.Count} visible items selected; {hiddenMatching} more selected items are hidden and were not selected under the current-visible rule", LogKind.Detail);
        }
    }

    /// <summary>全部取消选中：清空整个选中集合（包括被筛选隐藏的已选项）。</summary>
    public void ClearMultiSelection() => ClearMultiSelection(rebuild: true);

    private void ClearMultiSelection(bool rebuild)
    {
        foreach (var row in _libraryView.AllFiles)
            if (row.IsMultiSelected) row.IsMultiSelected = false;
        NotifyMultiSelectionChanged();
        if (rebuild) RefreshLibraryRows(force: true);
    }

    // ── 用户分组（N1） ───────────────────────────────────────────────────────

    /// <summary>「全部」+ 各分组；仅本次运行，不写 Settings。</summary>
    public ObservableCollection<GroupFilterOption> GroupFilterOptions { get; } = new() { new("All", true) };

    public string ActiveGroupName
    {
        get => _activeGroupName;
        set
        {
            if (_rebuildingGroupOptions) return;
            var normalized = string.IsNullOrWhiteSpace(value) ? "All" : value;
            if (!string.Equals(normalized, "All", StringComparison.Ordinal) &&
                (!_groupRowIds.TryGetValue(normalized, out var members) || members.Count == 0))
                normalized = "All"; // 空分组不可选中
            if (string.Equals(normalized, _activeGroupName, StringComparison.Ordinal)) return;
            _activeGroupName = normalized;
            ApplyGroupFilter();
            OnPropertyChanged(nameof(ActiveGroupName));
        }
    }

    /// <summary>把当前分组成员 Id 下发给 LibraryView（全部 = null）。</summary>
    private void ApplyGroupFilter()
    {
        if (string.Equals(_activeGroupName, "All", StringComparison.Ordinal) ||
            !_groupRowIds.TryGetValue(_activeGroupName, out var ids))
            _libraryView.GroupRowIds = null;
        else
            _libraryView.GroupRowIds = ids;
        RefreshLibraryRows(force: true);
    }

    private void ReloadGroups(IReadOnlyList<LibraryRow> rows)
    {
        _groupFile = GroupStore.Load(AppPaths.DataDirectory, out var warning);
        if (warning is not null) AppendLog(warning, LogKind.Error);
        var resolved = GroupStore.Resolve(_groupFile, rows);
        _groupRowIds = resolved.RowIds;
        if (resolved.Changed) SaveGroups();

        RebuildGroupOptions();
    }

    /// <summary>重建下拉集合时避免 ComboBox 的瞬时 null 把当前分组重置成「全部」。</summary>
    private void RebuildGroupOptions()
    {
        var active = _activeGroupName;
        _rebuildingGroupOptions = true;
        try
        {
            GroupFilterOptions.Clear();
            GroupFilterOptions.Add(new GroupFilterOption("All", true));
            foreach (var group in _groupFile.Groups)
            {
                var selectable = _groupRowIds.TryGetValue(group.Name, out var members) && members.Count > 0;
                GroupFilterOptions.Add(new GroupFilterOption(group.Name, selectable));
            }
        }
        finally
        {
            _rebuildingGroupOptions = false;
        }
        if (!GroupFilterOptions.Any(o => string.Equals(o.Name, active, StringComparison.Ordinal)) ||
            (!string.Equals(active, "All", StringComparison.Ordinal) &&
             !GroupFilterOptions.Any(o => string.Equals(o.Name, active, StringComparison.Ordinal) && o.IsSelectable)))
            active = "Failed to save group: ";
        _activeGroupName = active;
        OnPropertyChanged(nameof(ActiveGroupName));
        ApplyGroupFilter();
    }

    private void RefreshGroupOptions()
    {
        RebuildGroupOptions();
    }

    private void SaveGroups()
    {
        if (!GroupStore.Save(_groupFile, AppPaths.DataDirectory, out var error))
            AppendLog("Failed to save group: " + error, LogKind.Error);
    }

    public string? CreateGroup(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Group name cannot be empty";
        if (string.Equals(name, "All", StringComparison.OrdinalIgnoreCase)) return "All is a reserved name";
        if (_groupFile.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "Group name already exists";
        _groupFile.Groups.Add(new UserGroup { Name = name });
        SaveGroups();
        RefreshGroupOptions();
        AppendLog($"New group: {name}", LogKind.Detail);
        return null;
    }

    public string? RenameGroup(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return "Group name cannot be empty";
        if (string.Equals(newName, "All", StringComparison.OrdinalIgnoreCase)) return "All is a reserved name";
        var group = _groupFile.Groups.FirstOrDefault(g => string.Equals(g.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (group is null) return "Group not found";
        if (_groupFile.Groups.Any(g => !ReferenceEquals(g, group) && string.Equals(g.Name, newName, StringComparison.OrdinalIgnoreCase)))
            return "Group name already exists";
        var wasActive = string.Equals(_activeGroupName, oldName, StringComparison.OrdinalIgnoreCase);
        group.Name = newName;
        if (_groupRowIds.Remove(oldName, out var groupIds)) _groupRowIds[newName] = groupIds;
        SaveGroups();
        RefreshGroupOptions();
        if (wasActive) ActiveGroupName = newName; else ApplyGroupFilter();
        return null;
    }

    public string? DeleteGroup(string name)
    {
        var group = _groupFile.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (group is null) return "Group not found";
        _groupFile.Groups.Remove(group);
        _groupRowIds.Remove(name);
        SaveGroups();
        if (string.Equals(_activeGroupName, name, StringComparison.OrdinalIgnoreCase)) _activeGroupName = "All";
        RefreshGroupOptions();
        ApplyGroupFilter();
        AppendLog($"Delete group: {name}", LogKind.Detail);
        return null;
    }

    public int AddSelectedToGroup(string name)
    {
        var group = _groupFile.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (group is null) { ReportStatus("Group not found"); return 0; }
        var rows = GetSelectedFileRows();
        if (rows.Count == 0) { ReportStatus("No files selected"); return 0; }
        var added = GroupStore.AddMembers(group, rows);
        if (added > 0) SaveGroups();
        ReloadGroupFilterAfterChange();
        ReportStatus(added > 0 ? $"Added {added} files to group {group.Name}" : "Selected files are already in the group");
        return added;
    }

    public int RemoveSelectedFromActiveGroup()
    {
        if (string.Equals(_activeGroupName, "All", StringComparison.Ordinal)) return 0;
        var group = _groupFile.Groups.FirstOrDefault(g => string.Equals(g.Name, _activeGroupName, StringComparison.OrdinalIgnoreCase));
        if (group is null) return 0;
        var rows = GetSelectedFileRows();
        if (rows.Count == 0) { ReportStatus("No files selected"); return 0; }
        var removed = GroupStore.RemoveMembers(group, rows);
        if (removed > 0) SaveGroups();
        ReloadGroupFilterAfterChange();
        ReportStatus(removed > 0 ? $"Removed {removed} files from group {group.Name}" : "Selected files are not in the current group");
        return removed;
    }

    private List<LibraryRow> GetSelectedFileRows()
    {
        if (IsMultiSelectMode)
            return _libraryView.AllFiles.Where(r => !r.IsGroup && r.IsMultiSelected).ToList();
        return SelectedLibraryRow is { IsGroup: false } row
            ? new List<LibraryRow> { row }
            : new List<LibraryRow>();
    }

    private void ReloadGroupFilterAfterChange()
    {
        var resolved = GroupStore.Resolve(_groupFile, _libraryView.AllFiles.ToList());
        _groupRowIds = resolved.RowIds;
        if (resolved.Changed) SaveGroups();
        RebuildGroupOptions();
    }

    /// <summary>分组下拉项；空分组 IsSelectable=false，由 ComboBoxItem 置灰且不可选。</summary>
    public sealed class GroupFilterOption
    {
        public GroupFilterOption(string name, bool isSelectable)
        {
            Name = name;
            IsSelectable = isSelectable;
        }

        public string Name { get; }
        public bool IsSelectable { get; }
    }

    /// <param name="onLayoutReady">
    /// 开库后立刻回调一次三栏布局（M4-b1）：窗口要在扫描前把比例/隐藏状态摆好，
    /// 否则冷扫几十秒期间用户会先看到默认布局再"jump"一下。
    /// </param>
    public async Task InitializeAsync(Action<LayoutState>? onLayoutReady = null)
    {
        if (_initialized) return;
        _initialized = true;

        // 顺序有讲究：
        // ① 先开库 + 应用布局（重启后不看到默认布局跳变）；
        // ② 启动不再先读库内容并 Build（用户 2026-11-08 定）：设备刷新后直接进入扫描，
        //    资料库由扫描结束后的 SyncLibrary 一次性构建，省掉启动时的一次全量 Build。
        await OpenDatabaseAsync();
        onLayoutReady?.Invoke(LoadLayout());

        await RefreshDevicesAsync();

        StartDevicePolling();
        await RunScanTrackedAsync();
    }

    // ── 界面布局（M4-b1，docs/01 §2 / §2.1） ─────────────────────────────────

    /// <summary>读三栏布局（比例 / 日志隐藏 / 浮层宽度）；库没打开时回默认。</summary>
    internal LayoutState LoadLayout() => LayoutStore.Load(_db);

    /// <summary>存三栏布局；失败只写文件日志（布局存不下来不该影响关窗）。</summary>
    internal void SaveLayout(LayoutState state) => LayoutStore.Save(_db, state, line => _fileLog?.Write(line));

    /// <summary>读网格列配置（顺序 / 宽度 / 显隐）；库没打开或没写过 → 默认 10 列（M4-b2）。</summary>
    internal IReadOnlyList<GridColumnState> LoadGridColumns() => LibraryColumns.Load(_db);

    /// <summary>存网格列配置：关窗时按实际列状态存一次（docs/06 §3.1）。</summary>
    internal void SaveGridColumns(IReadOnlyList<GridColumnState> states) =>
        LibraryColumns.Save(_db, states, line => _fileLog?.Write(line));

    /// <summary>关窗：取消所有泵与子进程，确保不留孤儿进程（B1 判据）；最后关库与文件日志。</summary>
    public async Task ShutdownAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        CancelIndexPrefetch();   // 后台预热任务进关窗链（2026-11-04 审查 P2）
        DetachAppEvents();
        try
        {
            try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }

            var pendingScan = _activeScanTask;
            if (pendingScan is { IsCompleted: false })
            {
                try
                {
                    var finished = await Task.WhenAny(pendingScan, Task.Delay(TimeSpan.FromSeconds(5)))
                        .ConfigureAwait(true);
                    if (!ReferenceEquals(finished, pendingScan))
                    {
                        _scanShutdownIncomplete = true;
                        AppendLog("Shutdown timeout: scan still running; skipping explicit release of the database and file log", LogKind.Detail);
                    }
                }
                catch
                {
                    // 取消导致的异常不阻断关窗
                }
            }

            await _queue.ShutdownAsync();

            // 图标解码任务也要收尾（进度 §5.2 第 8 条）：最多等 2 s，超时不阻断关窗
            for (var i = 0; i < 20 && !_pendingIcons.IsEmpty; i++)
            {
                await Task.Delay(100).ConfigureAwait(true);
            }

            // 最后一道兜底：正常情况下取消已杀干净，这里只补漏网的进程树（07 §3.3）
            var killed = ProcessRunner.KillAll();
            AppendLog(killed > 0
                ? $"Canceled all queues and child processes: terminated {killed} leftover processes"
                : "Canceled all queues and child processes", LogKind.Detail);
        }
        catch (Exception ex)
        {
            AppendLog("Failed to shut down queues: " + ex.Message, LogKind.Detail);
        }
        finally
        {
            AppendLog("Application exiting", LogKind.Detail);
            FlushLogBatchNow();   // 复审 P2-G：关窗必须把最后一批日志刷上屏（否则"Application exiting"停在 pending）
            if (_queue.ShutdownIncomplete || _scanShutdownIncomplete)
            {
                // 泵/扫描还没退出：此时释放库会让它拿到已释放对象，进程马上退出即可（审核 P1-4）。
            }
            else
            {
                _fileLog?.Dispose();
                _db?.Dispose();
            }
        }
    }

    /// <summary>全局未处理异常入口（可能来自任意线程）。</summary>
    private void OnUnhandledError(string message) => ReportError("Unhandled exception " + message);

    /// <summary>退订 App 级静态事件（关窗必调；单测里也要调，否则静态事件会留住 VM）。</summary>
    internal void DetachAppEvents() => App.UnhandledErrorReported -= OnUnhandledError;

    /// <summary>异常统一出口：状态红字 + 日志（用户拍板的两处可见）。</summary>
    private void ReportError(Exception ex) =>
        ReportError(ex.GetType().Name + ": " + ex.Message);

    private void ReportError(string message)
    {
        // 关窗期第一判据（用户 2026-10-31 提前点出）：关窗时 UI 正在拆、库与文件日志可能已 Dispose，
        // 这时候投递 UI 状态没有意义，落文件日志也只会写进已关闭的 FileLog（内部吞异常 → 记录丢失）。
        // 关窗期只留 crash.log，不碰状态栏与日志集合。
        if (_shuttingDown)
        {
            CrashReporter.Write("MainViewModel.ReportError (shutdown)", new Exception(message));
            return;
        }

        // 全局异常可能从 AppDomain / 线程池线程进来：ObservableCollection 跨线程必抛，
        // 原来被 try{}catch{} 吞掉 → 日志只剩 crash.log。这里统一切回 UI 线程（审核 P1-2）。
        if (!RunOnUi(() =>
            {
                StatusMessage = message;
                StatusIsError = true;
                try { AppendLog("Error: " + message, LogKind.Error); } catch { }
            }))
        {
            // Dispatcher 已异常关闭（没走到上面的 _shuttingDown 分支）：至少把原因留在文件日志里
            try { _fileLog?.Write("Error: " + message); } catch { }
        }
    }

    /// <summary>
    /// 在 UI 线程上执行 <paramref name="action"/>（已在 UI 线程则直接执行，后台线程则投递）。
    /// 返回 false = Dispatcher 已关闭，动作没执行。
    /// </summary>
    private static bool RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return true;
        }

        try
        {
            dispatcher.BeginInvoke(action);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>普通提示：状态栏白字。</summary>
    private void ReportStatus(string message)
    {
        StatusMessage = message;
        StatusIsError = false;
    }

    /// <summary>b3b：一批日志上屏后触发一次（code-behind 用它滚一次到底，**不再在 CollectionChanged 里滚**）。</summary>
    public event Action? LogLinesFlushed;

    /// <summary>日志入口（b3a/b3b）：**所有**行都进全量缓冲与文件日志（简洁模式"Do not display but record"）；界面按类别过滤并 50 ms 批量上屏。</summary>
    private void AppendLog(LogLine line)
    {
        if (_shuttingDown) return;   // 审查 P2-2：关窗后文件日志/UI 集合可能已释放，迟到的后台日志直接丢弃

        _allLines.Add(line);
        while (_allLines.Count > MaxLogLines) _allLines.RemoveAt(0);
        _fileLog?.Write(line.FormatForFile());   // b3b：界面/文件格式单一来源（复审 P3-I）

        if (!LogFilter.ShouldShow(line, _logMode)) return;

        if (_uiTimer is null)
        {
            AppendLogLine(line);        // 单测/未注入：保持同步可见
            LogLinesFlushed?.Invoke();
            return;
        }

        _pendingLogLines.Add(line);
        while (_pendingLogLines.Count > MaxLogLines) _pendingLogLines.RemoveAt(0);
        _logFlushTimer ??= _uiTimer.ScheduleOnce(LogBatchInterval, FlushLogBatch);
    }

    /// <summary>b3b：把攒着的行一次上屏，并通知视图滚一次。</summary>
    private void FlushLogBatch()
    {
        _logFlushTimer = null;
        if (_pendingLogLines.Count == 0) return;
        foreach (var pending in _pendingLogLines) AppendLogLine(pending);
        _pendingLogLines.Clear();
        LogLinesFlushed?.Invoke();
    }

    /// <summary>非错误状态栏提示（右键复制等轻量操作反馈用）。</summary>
    internal void ShowStatus(string message) => ReportStatus(message);

    /// <summary>
    /// 清空日志面板（B4 收尾，2026-11-04 用户定：**只清界面**，不动 `logs\app.log` 文件日志）。
    /// 同时丢掉尚在 50 ms 批量里的待上屏行，避免"Cleared and reappeared"。
    /// </summary>
    public void ClearLog()
    {
        _logFlushTimer?.Dispose();
        _logFlushTimer = null;
        _pendingLogLines.Clear();
        _allLines.Clear();
        LogLines.Clear();
        LogLinesFlushed?.Invoke();
    }

    /// <summary>日志导出的数据源：**全量内存缓冲**（不受简洁模式过滤影响）。</summary>
    internal IReadOnlyList<LogLine> AllLogLines => _allLines;

    /// <summary>
    /// 导出全量日志到 txt（2026-11-01 用户要求）：UTF-8 带 BOM，写盘在后台线程。
    /// 成功/失败都记一条日志（失败用 Error 类别，简洁模式也能看到）。
    /// </summary>
    internal async Task ExportLogAsync(string path)
    {
        var lines = _allLines.ToArray();
        var text = LogExport.Build(lines, DateTime.Now);
        try
        {
            await Task.Run(() => File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                .ConfigureAwait(true);
            AppendLog($"Logs exported: {path} ({lines.Length} lines)", LogKind.End);
        }
        catch (Exception ex)
        {
            AppendLog("Failed to export logs: " + ex.GetType().Name + ": " + ex.Message, LogKind.Error);
        }
    }

    /// <summary>测试用：直接往日志通道塞一行（AppendLog 是私有的）。</summary>
    internal void AppendLogForTest(string text, LogKind kind = LogKind.Detail) => AppendLog(new LogLine(text, kind));

    /// <summary>测试用：网格视图（搜索防抖阈值判断依赖它的行数）。</summary>
    internal LibraryView LibraryViewForTest => _libraryView;

    /// <summary>测试/关窗用：立刻刷掉待上屏的日志（不等 50 ms）。</summary>
    internal void FlushLogBatchNow()
    {
        _logFlushTimer?.Dispose();
        _logFlushTimer = null;
        FlushLogBatch();
    }

    /// <summary>
    /// 简写：不写 kind 就是过程行（Detail）。**分界行必须显式传 Start / End / Error**（b3a 复审 P0-1 口径）。
    /// 设备相关的行请把 <paramref name="device"/> 传进来（复审 P2-F：别一律当全局行，也**不要**再手拼 `[设备名]` 前缀）。
    /// </summary>
    private void AppendLog(string text, LogKind kind = LogKind.Detail, DeviceInfo? device = null)
        => AppendLog(new LogLine(text, kind,
            udid: device?.Udid, deviceName: device?.Name, productType: device?.Product));

    private void AppendLogLine(LogLine line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    /// <summary>切模式：改内存状态 → 重建界面日志 → 记进 Settings（失败只记日志，不影响切换）。</summary>
    private async Task ToggleLogModeAsync()
    {
        _logMode = _logMode == LogMode.Simple ? LogMode.Detail : LogMode.Simple;
        RebuildLogLines();
        OnPropertyChanged(nameof(LogModeButtonText));

        if (_db is null) return;

        var value = LogFilter.ToSettingValue(_logMode);
        try
        {
            await Task.Run(() => SettingsStore.SetString(_db, SettingsStore.LogPanelMode, value));
            var modeName = _logMode == LogMode.Simple ? "Compact" : "Detailed";
            AppendLog($"Log panel switched: {modeName} ({SettingsStore.LogPanelMode}={value})", LogKind.Detail);
        }
        catch (Exception ex)
        {
            ReportError("Failed to save log mode: " + ex.Message);
        }
    }

    /// <summary>按当前模式从全量缓冲重建界面日志（从简洁切回详细时，之前隐藏的行立刻出现）。</summary>
    private void RebuildLogLines()
    {
        _logFlushTimer?.Dispose();
        _logFlushTimer = null;
        _pendingLogLines.Clear();

        LogLines.Clear();
        foreach (var line in _allLines)
        {
            if (LogFilter.ShouldShow(line, _logMode)) AppendLogLine(line);
        }

        LogLinesFlushed?.Invoke();
    }

    /// <summary>当前屏幕缩放（图标按它算目标像素；拿不到时按 1.0）。</summary>
    private static double DpiScale()
    {
        try
        {
            var source = System.Windows.PresentationSource.FromVisual(System.Windows.Application.Current?.MainWindow);
            if (source?.CompositionTarget is { } target) return target.TransformToDevice.M11;
        }
        catch
        {
            // 无窗口 / 单元测试环境：退回 1.0
        }

        return 1.0;
    }

    /// <summary>读整数设置；坏值 / 缺省回退。</summary>
    /// <summary>读非负整数设置（搜索防抖允许 0；坏值回 fallback）。</summary>
    private static int ReadNonNegativeIntSetting(AppDatabase db, string key, int fallback)
    {
        var raw = SettingsStore.GetString(db, key);
        return int.TryParse(raw?.Trim().Trim('"'), out var value) && value >= 0 ? value : fallback;
    }

    private static int ReadIntSetting(AppDatabase db, string key, int fallback)
    {
        var raw = SettingsStore.GetString(db, key);
        return int.TryParse(raw?.Trim().Trim('"'), out var value) && value > 0 ? value : fallback;
    }

    // ── 数据库与扫描（B7 / M2） ───────────────────────────────────────────────

    private async Task OpenDatabaseAsync()
    {
        try
        {
            var databasePath = Path.Combine(AppPaths.DataDirectory, "library.db");
            var db = await Task.Run(() => AppDatabase.Open(databasePath));
            // 日志默认关闭（用户 2026-10-30 拍板：release 默认不写 app.log；crash.log 仍然写）。
            // 设置键 Log.ToFile=true 可显式打开，便于排障。
            var logToFile = await Task.Run(() => SettingsStore.GetBool(db, SettingsStore.LogToFile, fallback: false));
        // b3a：日志面板模式（默认简洁；坏值回默认）。此时还没有日志，重建一次即可。
        var modeRaw = await Task.Run(() => SettingsStore.GetString(db, SettingsStore.LogPanelMode));

        // 搜索范围 / 匹配方式（2026-11-01 用户定：全部列 + 包含/前缀/精确）
        var scopeRaw = await Task.Run(() => SettingsStore.GetString(db, LibraryPrefs.SearchScopeSettingKey));
        var scope = SearchScope.Parse(scopeRaw);
        foreach (var field in Enum.GetValues<SearchField>())
            _searchScope.Set(field, scope.Includes(field));

        var searchModeRaw = await Task.Run(() => SettingsStore.GetString(db, LibraryPrefs.SearchModeSettingKey));
        _searchMatchMode = LibraryPrefs.ParseSearchMode(searchModeRaw);

        _libraryView.Scope = _searchScope;
        _libraryView.MatchMode = _searchMatchMode;
        _searchScope.PropertyChanged += OnSearchScopeChanged;
        OnPropertyChanged(nameof(SearchScope));
        OnPropertyChanged(nameof(SearchMatchMode));
        OnPropertyChanged(nameof(SearchMatchModeText));

        // 资料库排序偏好（2026-11-01）：坏值回默认 AppSize 降序
        var sortRaw = await Task.Run(() => SettingsStore.GetString(db, LibraryPrefs.SortSettingKey));
        _libraryView.Sort = LibraryPrefs.ParseSort(sortRaw, _libraryView.Sort);
        _defaultSort = new LibrarySort(_libraryView.Sort.Field, _libraryView.Sort.Descending);
        _defaultScope = SettingsDraft.CloneScope(_searchScope);
        OnPropertyChanged(nameof(LibrarySort));

        // 搜索防抖（2026-11-04 可配置）：行数阈值 / 延迟 ms；0 也合法，不能用 ReadIntSetting（它会吃 0）
        _searchDebounceRowThreshold = LibraryPrefs.ClampSearchDebounceRows(
            ReadNonNegativeIntSetting(db, LibraryPrefs.SearchDebounceRowsSettingKey, LibraryPrefs.DefaultSearchDebounceRows));
        _searchDebounce = TimeSpan.FromMilliseconds(LibraryPrefs.ClampSearchDebounceMs(
            ReadNonNegativeIntSetting(db, LibraryPrefs.SearchDebounceMsSettingKey, LibraryPrefs.DefaultSearchDebounceMs)));
        _deviceFamilyRetinaText = await Task.Run(() => SettingsStore.GetBool(db, LibraryPrefs.DeviceFamilyRetinaSettingKey, fallback: true));
        LibraryRow.UseRetinaText = _deviceFamilyRetinaText;

        // 资料库行高（2026-11-01）：坏值回默认 32，范围 16–48 在属性 setter 里夹紧
        var rowHeightRaw = await Task.Run(() => SettingsStore.GetString(db, "Grid.RowHeight"));
        if (double.TryParse(rowHeightRaw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var savedRowHeight))
        {
            LibraryRowHeight = savedRowHeight;
        }
        _logMode = LogFilter.ParseMode(modeRaw);
        OnPropertyChanged(nameof(LogModeButtonText));
        _logToFile = logToFile;

        // 资料库行为（2026-11-02 起持久化；默认：合并开 / 显示缺失开 / 兼容过滤关）
        _libraryView.MergeSameBundle = await Task.Run(() => SettingsStore.GetBool(db, "Grid.MergeSameBundle", fallback: true));
        _libraryView.ShowMissingFiles = _showMissingFiles = await Task.Run(() => SettingsStore.GetBool(db, "Grid.ShowMissingFiles", fallback: true));
        var compatRaw = await Task.Run(() => SettingsStore.GetString(db, "Grid.CompatMode"));
        var compatMode = ParseCompatMode(compatRaw);
        if (string.IsNullOrWhiteSpace(compatRaw))
        {
            // 兼容旧键：Grid.CompatibleOnly=true → 可安装
            var legacy = await Task.Run(() => SettingsStore.GetBool(db, "Grid.CompatibleOnly", fallback: false));
            if (legacy) compatMode = CompatFilterMode.Installable;
        }
        _compatFilterMode = compatMode;
        _libraryView.CompatFilter = compatMode;

        var advancedRaw = await Task.Run(() => SettingsStore.GetString(db, "Grid.AdvancedFilter"));
        _advancedFilter = AdvancedFilterCodec.Parse(advancedRaw);
        _libraryView.AdvancedFilter = _advancedFilter;
        OnPropertyChanged(nameof(AdvancedFilterEnabled));
        OnPropertyChanged(nameof(AdvancedFilterButtonText));
        OnPropertyChanged(nameof(LibraryMergeSameBundle));
        OnPropertyChanged(nameof(ShowMissingFiles));
        OnPropertyChanged(nameof(MissingFilterMenuText));
        OnPropertyChanged(nameof(CompatFilterMode));

        // 设备（2026-11-02）：轮询间隔 1–10 s，断连自动继续默认关
        _devicePollSeconds = Math.Clamp(ReadIntSetting(db, "Device.PollSeconds", 2), 1, 10);
        _autoResume = await Task.Run(() => SettingsStore.GetBool(db, "Device.AutoResumeAfterReconnect", fallback: true));
        _autoRetryAttempts = Math.Clamp(ReadIntSetting(db, "Queue.AutoRetryAttempts", 2), 2, 5);
        _reconnectStableSeconds = Math.Clamp(ReadIntSetting(db, "Device.ReconnectStableSeconds", 3), 1, 10);
        _queue.AutoRetryAttempts = _autoRetryAttempts;

        // 图标 / 扫描参数（2026-11-02：设置窗口可改，改完重建扫描器）
        _iconCacheMaxMb = Math.Clamp(ReadIntSetting(db, IconCacheOptions.CacheMaxMbKey, IconCacheOptions.DefaultCacheMaxMb), 64, 20480);
        _iconMatchDisplayScale = await Task.Run(() => SettingsStore.GetBool(db, IconCacheOptions.MatchDisplayScaleKey, fallback: true));
        _iconDownscaleCache = await Task.Run(() => SettingsStore.GetBool(db, IconCacheOptions.DownscaleCacheKey, fallback: true));
        _scanBatchSize = ScanOptions.ResolveBatchSize(await Task.Run(() => SettingsStore.GetString(db, ScanOptions.BatchSizeKey)));
            _fileLog = new FileLog(AppPaths.LogsDirectory, logToFile);
            if (_fileLog.OpenError is { } fileLogError)
                AppendLog("Failed to open file log; disabled (no functional impact): " + fileLogError, LogKind.Error);

            // b3a：模式来源写进日志，便于确认"memory"是否生效（设置键 Log.PanelMode，默认简洁）
            AppendLog($"Log panel: {(_logMode == LogMode.Simple ? "简洁" : "详细")} (setting {SettingsStore.LogPanelMode}={modeRaw ?? "(空)"}）", LogKind.Detail);
            RebuildLogLines();

            _db = db;

            // M4-b4：设备信息行字段配置（接口预留——设置面板以后接；坏值回默认）。默认集 = 名称/型号/版本/容量/颜色/电池健康/ECID。
            var infoFieldsRaw = SettingsStore.GetString(db, DeviceInfoFields.SettingsKey);
            DeviceInfo.Fields = DeviceInfoFields.Parse(infoFieldsRaw);

            var queueColumnsRaw = SettingsStore.GetString(db, QueueColumns.SettingsKey);
            _queueColumnSettings = QueueColumns.Parse(queueColumnsRaw);
            ApplyQueueColumnsToDevices();
            AppendLog($"Device info fields: {string.Join(" · ", DeviceInfoFields.OrderedVisible(DeviceInfo.Fields)
                .Select(DeviceInfoFields.ToId))}（设置键 {DeviceInfoFields.SettingsKey}={(infoFieldsRaw is null ? "(not set)" : "Set")}）", LogKind.Detail);

            // M3：图标缓存参数从设置读；显示尺寸等 M4 定 UI 后调一次 RegisterDisplaySize 即可
            // （2026-11-02 起：这些值都在字段里，设置窗口应用时用 CreateScanner 重建，参数读法只有一处）
            CreateScanner(db);

            AppendLog($"Database ready: {databasePath} (user_version={db.UserVersion}, journal_mode={db.JournalMode}, " +
                      $"busy_timeout={db.BusyTimeout} ms, file log={(logToFile ? "开" : "关")}）", LogKind.Detail);
            var iconReq = _iconRequest ?? default;
            AppendLog($"Icons: display {IconDisplayPx} px × scale {iconReq.DpiScale:F2} (target {iconReq.TargetPx} px), " +
                      $"Tiers [{string.Join(",", _iconOptions!.Buckets)}], cache limit {_iconOptions.CacheMaxMb} MB, " +
                      $"Match by scale={(_iconMatchDisplayScale ? "开" : "关")}, downscale to tier={(_iconDownscaleCache ? "开" : "关")}", LogKind.Detail);
            AppendLog($"Scan batches: save every {_scanBatchSize} rows (setting key {ScanOptions.BatchSizeKey}); " +
                      $"Device polling {_devicePollSeconds} s; auto-resume on connect={(_autoResume ? "开" : "关")}, reconnect stable window {_reconnectStableSeconds} s, auto-retry failures {_autoRetryAttempts} times (setting keys Device.PollSeconds / Device.AutoResumeAfterReconnect / Device.ReconnectStableSeconds / Queue.AutoRetryAttempts)", LogKind.Detail);
        }
        catch (Exception ex)
        {
            _dbError = "Database unavailable (" + ex.GetType().Name + "）：" + ex.Message;
            // 数据目录可能不可写，但 logs 目录是启动自检保证过的：至少把原因留在文件里
            _fileLog ??= new FileLog(AppPaths.LogsDirectory, enabled: true);
            _fileLog.Write(_dbError);
            // 用户 2026-10-31：开库失败必须**红字可见**（否则界面空白、以为"No data"）
            ReportError(_dbError + "— Library scan and persistence unavailable (data directory: " + AppPaths.DataDirectory + "）");
        }
    }

    /// <summary>
    /// 删除资料库中所有「文件不存在」的记录（2026-11-07 用户定：右键菜单、不弹确认、不动磁盘文件）。
    /// </summary>
    public async Task<int> DeleteMissingRecordsAsync()
    {
        if (_db is null)
        {
            ReportError("Failed to delete missing-file records: database not ready");
            return 0;
        }

        var db = _db;
        try
        {
            var deleted = await Task.Run(() => PackageStore.DeleteMissing(db));
            if (deleted == 0)
            {
                ReportStatus("No records with missing files");
                AppendLog("Deleted missing-file records: 0", LogKind.Detail);
                return 0;
            }

            var rows = await Task.Run(() => PackageStore.LoadAll(db));
            SyncLibrary(rows);
            AppendLog($"Deleted missing-file records: {deleted}", LogKind.Detail);
            ReportStatus($"Deleted {deleted} missing-file records");
            return deleted;
        }
        catch (Exception ex)
        {
            ReportError("Failed to delete missing-file records: " + ex.GetType().Name + ": " + ex.Message);
            return 0;
        }
    }

    /// <summary>扫描 IPA 目录：库中已有内容先上屏，再增量对账 + 解析。</summary>
    private async Task ScanIpasAsync()
    {
        CancelIndexPrefetch();   // 新一轮扫描：旧索引任务立即作废
        if (_db is null || _scanner is null)
        {
            ReportError("Skipping scan: " + (_dbError ?? "Database not ready"));
            return;
        }

        // 每轮清零：否则手动刷新多轮后"Parse N batches on screen in batches"是进程累计值（审核 P2-1）。
        _parsedBatchCount = 0;
        _parsedBatchRows = 0;

        try
        {
            IsScanningLibrary = true;
            ScanProgressText = "Scanning library…";
            _libraryRefreshPending = false;

            // 先问"Can the library be written?"（2 s 内给结论）：否则下面的 ScanRoots 写入会等满 busy_timeout，
            // 用户要等十几秒才看到"Database is not writable"（审核 P2-3 补充实测）。放后台线程，不冻 UI。
            var probeError = await Task.Run(() => _scanner.ProbeDatabase());
            if (probeError is not null)
            {
                ReportError("Failed to write to the database; skipping this scan: " + probeError);
                return;
            }

            var roots = await EnsureScanRootsAsync();
            if (roots.Count == 0)
            {
                ReportError("No scan roots available; please add a scan root");
                return;
            }

            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Total <= 0) return;
                ScanProgressText = p.Done >= p.Total
                    ? $"Scanning library… ({p.Phase} completed: {p.Done})"
                    : $"Scanning library… ({p.Phase} {p.Done} / {p.Total})";
                if (p.Done >= p.Total) AppendLog($"Scan {p.Phase} phase completed: {p.Done}", LogKind.Detail);
            });

            var forceReparse = _forceReparseForNextScan;
            _forceReparseForNextScan = false;

            var summaries = new List<string>();
            var saveFailed = 0;
            var saveAborted = 0;
            var rootErrors = new List<string>();
            var dataChanged = false;
            ScanResult? last = null;
            foreach (var root in roots)
            {
                // 扫描被跳过（扫描根不可访问 / 库不可写）：记下来，**继续扫其它根**（审查 B5）
                if (!Directory.Exists(root.Path))
                {
                    AppendLog($"Scan root inaccessible; skipping: {root.Path}", LogKind.Error);
                    rootErrors.Add($"{root.Path} (directory not found)");
                    continue;
                }

                var result = await Task.Run(
                    () => _scanner.ScanAsync(root.Id, root.Path, progress, _lifetimeCts.Token, forceReparse),
                    _lifetimeCts.Token);

                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    // 审查 B5：单个根失败不再 return（否则后面的根全不扫）；汇总后统一红字
                    AppendLog($"Scan root problem: {root.Path} — {result.Error}", LogKind.Error);
                    rootErrors.Add($"{root.Path}：{result.Error}");
                    continue;
                }

                last = result;
                dataChanged |= result.HasLibraryChange;
                summaries.Add(result.Summary);
                saveFailed += result.SaveFailed;
                saveAborted += result.SaveAborted;
                AppendLog($"Scan root: {root.Path} (ScanRootId={root.Id})", LogKind.Detail);
                AppendLog(result.Summary, LogKind.End);
            }

            if (last is null)
            {
                // 审查 B5：所有根都没扫成 → 必须有红字（旧实现静默 return，状态栏什么也不说）
                ReportError("Scan incomplete: no scan roots available" +
                            (rootErrors.Count > 0 ? "（" + string.Join("；", rootErrors) + "）" : ""));
                return;
            }

            var rows = await Task.Run(() => PackageStore.LoadAll(_db));

            // 扫描期间不上屏，结束时必须构建一次列表（即使数据没变化；启动时列表就是空的）。
            // 无变化时保留已有排序索引，只重建视图。
            _applyingScanResults = true;
            try { SyncLibrary(rows); }
            finally { _applyingScanResults = false; _libraryRefreshPending = false; }
            AppendLog(!dataChanged && _libraryView.HasValidPrefetch
                ? "Scan unchanged: rebuilding the list with the existing sort index"
                : $"Parse and save: {_parsedBatchCount} batches / {_parsedBatchRows} rows ({roots.Count} scan roots)", LogKind.Detail);
            var lastResult = last;
            var resultSummary = string.Join("；", summaries);

            if (saveFailed > 0 || saveAborted > 0)
            {
                // 用户 2026-10-30 拍板：结果没保存就等于失败，直接报失败（不建兜底文件）。
                // 准确说法（审核 P2-6）：**已提交的批次保留**，失败/放弃的批次行仍是 Pending，
                // 下次扫描重来 —— 所以是"The results of this round are incomplete"，不是"Results discarded"。
                var lost = saveFailed + saveAborted;
                ReportError($"Scan incomplete: {lost} batches could not be written to the library (database busy or read-only)" +
                            (saveAborted > 0 ? $"({saveAborted} batches abandoned after repeated failures)" : "") +
                            "; committed batches are kept; unwritten rows will be retried on the next scan");
                return;
            }

            if (rootErrors.Count > 0)
            {
                // 部分根失败：界面仍展示已扫到的结果，但红字提醒"incomplete"（审查 B5）
                ReportError($"Scan incomplete: {rootErrors.Count} scan roots failed (results already scanned were updated): " +
                            string.Join("；", rootErrors));
            }
            else
            {
                ReportStatus(resultSummary);
            }

            // 扫描完全成功后才后台预热排序索引（失败/部分失败不建，避免基于不完整结果）
            if (rootErrors.Count == 0 && saveFailed == 0 && saveAborted == 0 &&
                (dataChanged || !_libraryView.HasValidPrefetch))
            {
                StartIndexPrefetch();
            }
        }
        catch (OperationCanceledException)
        {
            // 关窗取消：这不是错误，静默收尾（体检报告 P1-5）
            AppendLog("Scan canceled (application closing)", LogKind.End);
        }
        catch (Exception ex)
        {
            // 扫描级失败也要红字可见（和库不可写同一口径，审核 P1-1）
            ReportError("Failed to scan IPA: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // 成功 / 失败 / 取消都要解除资料库禁用，恢复真实网格。
            // 扫描期间用户改过搜索/筛选/合并等设置 → 这里按最新设置补一次刷新。
            IsScanningLibrary = false;
            ScanProgressText = "Scanning library…";
            if (_libraryRefreshPending)
            {
                _libraryRefreshPending = false;
                RefreshLibraryRows(force: true);
            }
        }
    }

    /// <summary>读（必要时初始化）扫描根列表：库里没有就加默认目录（多根支持，2026-11-02）。</summary>
    private async Task<IReadOnlyList<ScanRootRow>> EnsureScanRootsAsync()
    {
        if (_db is null) return Array.Empty<ScanRootRow>();

        if (_scanRoots.Count == 0)
        {
            var rows = await Task.Run(() => ScanRootStore.List(_db));
            if (rows.Count == 0)
            {
                var defaultFolder = _ipaFolder;
                var (added, error) = await Task.Run(() =>
                {
                    try { Directory.CreateDirectory(defaultFolder); } catch { /* Add will report missing folder */ }
                    return ScanRootStore.Add(_db, defaultFolder);
                });
                if (added is not null) rows.Add(added);
                else AppendLog($"Failed to add the default scan root: {error}", LogKind.Error);
            }

            _scanRoots.Clear();
            _scanRoots.AddRange(rows.Where(r => r.Enabled));
        }

        // 2026-11-02：编号映射（RootN）补齐后重算"Path column display"（老库首次启动也会补编号）
        if (_db is not null)
        {
            var roots = _scanRoots;
            _rootNumbers = await Task.Run(() => ScanRootNumbers.Ensure(_db, roots));
        }
        RebuildRootDisplay();

        return _scanRoots;
    }

    /// <summary>添加一个扫描目录并立刻扫描（2026-11-02 用户定：「添加… → 添加目录…」）。</summary>
    public async Task AddRootAsync(string path)
    {
        var (ok, error) = await AddScanRootAsync(path);
        if (!ok) ReportError("Failed to add scan directory: " + (error ?? "Unknown error"));
    }

    /// <summary>
    /// 「刷新」（2026-11-02 用户定：`⟳` 与右键同一入口，**与「清空已结束」合并**）：
    /// ① 清空**当前选中设备**队列里已结束的任务（复用 <see cref="DeviceQueueViewModel.ClearFinished"/>）；
    /// ② 清**当前设备**的绿色记录（别的设备不受影响）；③ 清资料库红/黄/绿标记（**勾保留**）；
    /// ④ 按当前队列重算（还在排队/运行 → 仍显示黄）。
    /// </summary>
    public void RefreshMarksAndClearFinished()
    {
        var device = SelectedDevice;
        var cleared = 0;
        if (device is not null)
        {
            // 服务端也清：否则刷新后重算会把失败项又标回红（快照里还有 Failed）
            _queue.RemoveFinished(device.Device.Udid);
            cleared = device.ClearFinished();          // 复用「清空已结束」的判定与实现（清界面行）
            _successMarks.Clear(device.Device.Udid);   // 绿只清当前设备（2026-11-02 用户定）
        }

        // 覆盖**全部文件行**（含组收起后不在网格里的成员），否则展开组时成员还带着旧红/黄/绿
        foreach (var row in _libraryView.AllFiles) row.Marker = LibraryMarker.None;

        // 重算：黄=仍在队列 / 红=已结束任务已清 / 绿=已清。
        // 勾（InstalledState）**不重算**（updateInstalled: false）——本动作的语义是"Clear mark, check and keep"，
        // 回读还没到（InstalledApps 为 null）时重算会把勾全清掉，违反用户口径。
        RefreshLibraryMarkers(updateInstalled: false);

        // 审查 A2（推荐方案 A，2026-11-03）：⟳ 顺带触发一次回读——否则"Manually deleted the app on the device → click Clear → cannot uncheck it"会一直陈旧，
        // 直到下一次安装/卸载或「刷新设备」。回读是后台的，不挡 UI；失败会写日志。
        if (device is not null) _ = PrefetchInstalledAsync(device.Device.Udid);

        AppendLog($"Refreshed: status markers cleared and {cleared} finished tasks removed", LogKind.End);
        ReportStatus("Refreshed: status markers cleared");
    }

    private bool _showMissingFiles = true;

    /// <summary>是否显示"File deleted"的行（2026-11-02 用户定：右键隐藏/显示）。</summary>
    public bool ShowMissingFiles
    {
        get => _showMissingFiles;
        set
        {
            if (!SetProperty(ref _showMissingFiles, value)) return;
            _libraryView.ShowMissingFiles = value;
            OnPropertyChanged(nameof(MissingFilterMenuText));
            RefreshLibraryRows(force: true);
        }
    }

    public string MissingFilterMenuText => _showMissingFiles ? "Hide missing files" : "Show missing files";

    public void ToggleMissingFiles()
    {
        ShowMissingFiles = !ShowMissingFiles;
        ReportStatus(ShowMissingFiles ? "Show missing files" : "Missing files hidden");
    }

        /// <summary>M4-a：解析批次回调（提交者线程）→ 只搬数据，集合更新投递回 UI（Progress 上下文）。</summary>
    private void PublishParsedBatch(IReadOnlyList<PackageRow> rows)
    {
        if (rows.Count == 0) return;
        _parsedBatchCount++;
        _parsedBatchRows += rows.Count;
        _batchProgress.Report(rows);      // UI 线程上执行 ApplyBatchRows
    }

    /// <summary>把一批已解析行并进网格（M4-b2，UI 线程）：按 `Packages.Id` 原地更新，新行追加。</summary>
    private void ApplyBatchRows(IReadOnlyList<PackageRow> rows)
    {
        // 扫描期间不上屏（2026-11-08 用户定）：只落库，扫描结束后 SyncLibrary 一次性构建。
        // 避免每批全量 Build 的平方级开销，也避免扫描中用户操作与行替换并发。
        if (_isScanningLibrary) return;
        _libraryView.Upsert(rows);
        RefreshLibraryRows(force: false);
    }

    // ── 资料库网格（M4-b2，docs/06 §3） ───────────────────────────────────────

    /// <summary>搜索词：名称 + BundleId + 文件名子串（大小写不敏感）。</summary>
    public string LibrarySearchText
    {
        // 打字期间 TextBox 立刻回显（_pendingSearchText），真正过滤在 ApplySearchNow 里做
        get => _pendingSearchText ?? _libraryView.SearchText;
        set
        {
            var text = value ?? "";
            if (string.Equals(LibrarySearchText, text, StringComparison.Ordinal)) return;

            _pendingSearchText = text;
            OnPropertyChanged(nameof(LibrarySearchText));

            _searchTimer?.Dispose();
            _searchTimer = null;

            // 日常（≤1000 行）不延迟；10k 规模才 300 ms 防抖，避免连打时每键整表重建
            if (_uiTimer is null || _libraryView.Rows.Count <= _searchDebounceRowThreshold)
            {
                ApplySearchNow();
                return;
            }

            _searchTimer = _uiTimer.ScheduleOnce(_searchDebounce, ApplySearchNow);
        }
    }

    /// <summary>是否还有待触发的搜索防抖（测试用；日志 flush 计时器不算）。</summary>
    internal bool HasPendingSearchDebounce => _searchTimer is not null;

    /// <summary>当前搜索防抖行数阈值（测试/设置回读用）。</summary>
    internal int SearchDebounceRowsForTest => _searchDebounceRowThreshold;

    /// <summary>当前搜索防抖延迟（测试/设置回读用）。</summary>
    internal TimeSpan SearchDebounceDelayForTest => _searchDebounce;

    /// <summary>把待定的搜索词真正应用到网格（防抖到期或规模小直接调用）。</summary>
    private void ApplySearchNow()
    {
        _searchTimer?.Dispose();
        _searchTimer = null;

        var text = _pendingSearchText ?? "";
        _pendingSearchText = null;
        if (string.Equals(_libraryView.SearchText, text, StringComparison.Ordinal)) return;

        _libraryView.SearchText = text;
        OnPropertyChanged(nameof(LibrarySearchText));
        RefreshLibraryRows(force: true);
    }

    /// <summary>同 BundleId 合并显示（默认开）。</summary>
    public bool LibraryMergeSameBundle
    {
        get => _libraryView.MergeSameBundle;
        set
        {
            if (_libraryView.MergeSameBundle == value) return;
            _libraryView.MergeSameBundle = value;
            OnPropertyChanged(nameof(LibraryMergeSameBundle));
            RefreshLibraryRows(force: true);
        }
    }

    /// <summary>网格当前排序（默认解压后体积降序）。</summary>
    public LibrarySort LibrarySort => _libraryView.Sort;

    /// <summary>切换搜索匹配方式并立即重排（2026-11-01 用户定）。</summary>
    public void SetSearchMatchMode(SearchMatchMode mode)
    {
        if (_searchMatchMode == mode) return;
        _searchMatchMode = mode;
        _libraryView.MatchMode = mode;
        OnPropertyChanged(nameof(SearchMatchMode));
        OnPropertyChanged(nameof(SearchMatchModeText));
        RefreshLibraryRows(force: true);
        SaveSetting(LibraryPrefs.SearchModeSettingKey, LibraryPrefs.FormatSearchMode(mode));
    }

    /// <summary>匹配方式文案（给 ▾ 菜单 ToolTip 用）。</summary>
    public string SearchMatchModeText => _searchMatchMode switch
    {
        SearchMatchMode.Prefix => "Prefix",
        SearchMatchMode.Exact => "Exact",
        _ => "Contains",
    };

    private void OnSearchScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 当前搜索范围只影响当前视图；默认范围由设置窗口保存（2026-11-08 用户要求）。
        RefreshLibraryRows(force: true);
    }

    /// <summary>
    /// 设置写入统一走后台（Task 内 try/catch；审查 N2/N9 的口径）。
    /// **2026-11-03 审查 N7 修复**：不再"One fire-and-forget per key"，改为入队 + 一次事务批量写；
    /// 失败会写日志（之前被吞掉），设置窗口「应用」会 await <see cref="FlushSettingsAsync"/> 确保写完。
    /// </summary>
    private void SaveSetting(string key, string value)
    {
        if (_db is null) return;
        lock (_settingsWriteGate) _pendingSettingsWrites.Add(new(key, value));
        _ = SafeFireAndForget.Run(FlushSettingsAsync, "Save settings");   // 常规路径自动落库（窗口"Apply"会再 await 一次确保完成）
    }

    /// <summary>把排队的设置一次事务写入（可 await）；失败写日志，不再静默吞掉（审查 N7）。</summary>
    internal Task FlushSettingsAsync()
    {
        if (_db is null) return Task.CompletedTask;

        List<KeyValuePair<string, string>> batch;
        lock (_settingsWriteGate)
        {
            if (_pendingSettingsWrites.Count == 0) return _settingsWriteTask;
            batch = new(_pendingSettingsWrites);
            _pendingSettingsWrites.Clear();
        }

        var db = _db;
        var task = Task.Run(() =>
        {
            try { SettingsStore.SetMany(db, batch); }
            catch (Exception ex) { AppendLog("Failed to save settings: " + ex.GetType().Name + ": " + ex.Message, LogKind.Error); }
        });
        _settingsWriteTask = task;
        return task;
    }

    /// <summary>点列头：切换排序、持久化到 <c>Grid.Sort</c> 并重排（2026-11-01 用户定：记忆偏好）。</summary>
    public void SetLibrarySort(LibrarySortField field, bool descending)
    {
        _libraryView.Sort = new LibrarySort(field, descending);
        OnPropertyChanged(nameof(LibrarySort));
        RefreshLibraryRows(force: true);
        // 当前排序只影响当前视图；默认排序由设置窗口保存（2026-11-08 用户要求）。
    }

    /// <summary>
    /// 双击组行：展开 / 收起该组（P-50k B1 快路径，2026-11-04）。
    /// 只改上次拼装好的行序列，不跑 filter/sort/group，不重算 count/root；
    /// 展开出的新可见行要补图标，选中被收起的成员行回落到组行。
    /// </summary>
    public void ToggleLibraryGroup(LibraryRow row)
    {
        if (!row.IsGroup) return;
        // 多选模式下还要保持「隐藏但已选」行追加在末尾：走完整刷新。
        if (IsMultiSelectMode)
        {
            _libraryView.ToggleGroupFast(row.GroupBundleId);
            RefreshLibraryRows(force: true);
            return;
        }
        var bundleId = row.GroupBundleId;
        var keep = _selectedRow;
        var perfTotal = PerfProbe ? Stopwatch.StartNew() : null;

        var rows = _libraryView.ToggleGroupFast(bundleId);
        var perfFast = perfTotal?.Elapsed.TotalMilliseconds ?? 0;
        perfTotal?.Restart();

        LibraryRows = rows;
        var restored = RestoreLibrarySelection(keep, rows, fallbackGroupBundleId: bundleId);
        if (!ReferenceEquals(restored, keep)) SelectedLibraryRow = restored;

        // 展开后成员行第一次进 LibraryRows：不补图标就要等下一次全量刷新（收起时无副作用）
        LoadMissingIcons();
        perfTotal?.Stop();
        PerfProbeLog(
            $"toggle bundle={bundleId} rows={rows.Count} fast={perfFast:F2} total={perfFast + (perfTotal?.Elapsed.TotalMilliseconds ?? 0):F2}");
    }

    /// <summary>
    /// 重建网格的行序列：过滤 → 合并 → 排序（全部在 <see cref="LibraryView"/> 的纯逻辑里做）。
    /// 批次期间按 150 ms 节流，避免每 30 行就整表刷一次；结束 / 用户操作时强制。
    /// </summary>
    private void RefreshLibraryRows(bool force)
    {
        // 扫描期间顶栏仍可预先设置，但网格不构建；等扫描结果应用时按最新设置一次构建。
        if (_isScanningLibrary && !_applyingScanResults)
        {
            _libraryRefreshPending = true;
            return;
        }
        if (!force && _libraryUiClock.ElapsedMilliseconds - _lastLibraryUiRefreshMs < LibraryRefreshThrottleMs) return;

        _lastLibraryUiRefreshMs = _libraryUiClock.ElapsedMilliseconds;
        var perfTotal = PerfProbe ? Stopwatch.StartNew() : null;

        // 路径列显示（RootN\子路径）要先下发到行，再产出视图序列（行对象是复用的）
        ApplyRootDisplayToRows();
        var perfRoot = perfTotal?.Elapsed.TotalMilliseconds ?? 0;
        perfTotal?.Restart();

        var rows = _libraryView.Build();
        _visibleFilteredRows = rows.Where(r => !r.IsGroup).ToList();

        // 多选模式：被当前搜索/筛选隐藏的已选文件行仍要可见，追加到末尾；
        // 清空搜索/筛选后 Build 会自然把它们放回正常位置。
        if (IsMultiSelectMode)
        {
            var visibleIds = rows.Select(r => r.Id).ToHashSet();
            var appended = _libraryView.AllFiles
                .Where(r => !r.IsGroup && r.IsMultiSelected && !visibleIds.Contains(r.Id))
                .ToList();
            if (appended.Count > 0)
            {
                // 与正常可见行区分：中间插一条窄分隔线（DataGridRowStyle 按 IsAppendedSeparator 画）。
                if (rows.Count > 0) rows.Add(LibraryRow.CreateAppendedSeparator());
                rows.AddRange(appended);
            }
        }

        var perfBuild = perfTotal?.Elapsed.TotalMilliseconds ?? 0;
        perfTotal?.Restart();

        // 重建期间保留选中：文件行按 Id 找（对象是复用的）；组行按 BundleId 找
        var keep = _selectedRow;
        LibraryRows = rows;
        var perfBind = perfTotal?.Elapsed.TotalMilliseconds ?? 0;
        perfTotal?.Restart();

        var restored = RestoreLibrarySelection(keep, rows, fallbackGroupBundleId: null);
        if (!ReferenceEquals(restored, keep)) SelectedLibraryRow = restored;
        var perfSelect = perfTotal?.Elapsed.TotalMilliseconds ?? 0;
        perfTotal?.Restart();

        // 2026-11-01 修 bug：计数按"The number of filtered files + the number of BundleIds that will be grouped"，与组展开/收起无关
        // （之前按可见行数算，展开 BundleId 会让"N packages"变化）
        var (files, groups, missing) = _libraryView.LastCounts;   // C3：Build 时顺带算好，省一趟全表
        var countText = groups > 0 ? $"{files} packages / {groups} groups" : $"{files} packages";
        if (missing > 0) countText += $", {missing} missing files";
        LibraryCountText = countText;
        var perfCount = perfTotal?.Elapsed.TotalMilliseconds ?? 0;
        perfTotal?.Restart();

        LoadMissingIcons();
        perfTotal?.Stop();
        var perfIcons = perfTotal?.Elapsed.TotalMilliseconds ?? 0;
        PerfProbeLog(
            $"refresh rows={rows.Count} root={perfRoot:F2} build={perfBuild:F2} bind={perfBind:F2} select={perfSelect:F2} " +
            $"count={perfCount:F2} icons={perfIcons:F2} total={perfRoot + perfBuild + perfBind + perfSelect + perfCount + perfIcons:F2}");
        ScheduleRefreshLibraryMarkers();
    }

    /// <summary>
    /// 重建/快路径之后恢复选中：文件行按 Id、组行按 BundleId。
    /// <paramref name="fallbackGroupBundleId"/> 非空时，若原本选中的成员行已被收起移除，选中回落到该组行。
    /// </summary>
    internal static LibraryRow? RestoreLibrarySelection(
        LibraryRow? keep, IReadOnlyList<LibraryRow> rows, string? fallbackGroupBundleId)
    {
        var restored = keep switch
        {
            null => null,
            { IsGroup: true } group => rows.FirstOrDefault(r => r.IsGroup && r.GroupBundleId == group.GroupBundleId),
            _ => rows.FirstOrDefault(r => !r.IsGroup && r.Id == keep.Id),
        };

        if (restored is null && fallbackGroupBundleId is not null && keep is { IsGroup: false })
        {
            restored = rows.FirstOrDefault(r => r.IsGroup && r.GroupBundleId == fallbackGroupBundleId);
        }

        return restored;
    }

    private const long LibraryRefreshThrottleMs = 150;

    // 性能探针（2026-11-04 入库，默认关闭）：IDEVICEGUI_PERF=1 时把资料库刷新/展开收起分段写 logs\perf.log。
    // 只在显式开环境变量时写文件；正常使用零开销、零文件。
    private static readonly bool PerfProbe = Environment.GetEnvironmentVariable("IDEVICEGUI_PERF") == "1";
    private static readonly string PerfProbePath = Path.Combine(Services.AppPaths.LogsDirectory, "perf.log");

    private static void PerfProbeLog(string line)
    {
        if (!PerfProbe) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PerfProbePath)!);
            File.AppendAllText(PerfProbePath, line + Environment.NewLine);
        }
        catch
        {
            // 探针失败不影响程序
        }
    }

    /// <summary>给还没有图标、但有缓存键的行排一次后台解码（并发 4，解码完 <c>Freeze()</c> 后回 UI 赋值）。</summary>
    private void LoadMissingIcons()
    {
        if (_shuttingDown) return;      // 关窗后不再起新任务（进度 §5.2 第 8 条：进关窗链）
        var dataDirectory = Services.AppPaths.DataDirectory;
        foreach (var row in LibraryRows)
        {
            var key = row.IconKey;
            if (key is null || row.Icon is not null) continue;
            if (_missingIcons.ContainsKey(key)) continue;         // 已知缺失/解不开：不再重试
            if (!_pendingIcons.TryAdd(row.Id, 0)) continue;      // 同一行只排一次

            _ = SafeFireAndForget.Run(() => Task.Run(async () =>
            {
                await _iconGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_shuttingDown) return;
                    var image = Services.Imaging.IconImageLoader.Load(dataDirectory, key);
                    if (image is null) _missingIcons.TryAdd(key, 0);   // 负缓存：键存在但解不出来
                    if (!_shuttingDown) _iconProgress.Report((row, key, image));
                }
                catch
                {
                    _missingIcons.TryAdd(key, 0);
                    if (!_shuttingDown) _iconProgress.Report((row, key, null));
                }
                finally
                {
                    _iconGate.Release();
                    _pendingIcons.TryRemove(row.Id, out _);
                }
            }), "Load icons");
        }
    }

    private void ApplyIcon((LibraryRow Row, string? Key, object? Image) payload)
    {
        // 等待期间行的图标键可能变了（重解析换了图标）：只有"Scheduling time key"仍然有效才赋值，
        // 否则会把旧图写回去、并让新图因去重永远不加载（审核 P2-3）
        if (!payload.Row.HasIconKey(payload.Key)) return;
        payload.Row.Icon = payload.Image;
    }

    /// <summary>包一层登记：扫描任务存入 <see cref="_activeScanTask"/>，关窗时统一等待（审核 P1-4/P1-5）。</summary>
    private Task RunScanTrackedAsync()
    {
        var task = ScanIpasAsync();
        _activeScanTask = task;
        return task;
    }

    /// <summary>
    /// 手动"Quick refresh"：走同一条增量扫描——按文件身份/路径 + 大小/mtime 对账，
    /// 没变的包直接复用库里的解析结果，只解析新增/变化的包。连点只跑一轮。
    /// </summary>
    private Task ManualScanAsync() => RunManualScanAsync(forceReparse: false, label: "Quick refresh");

    /// <summary>
    /// 全量重扫：与快速刷新走同一条扫描，但解析阶段**连 Ok 行也重解析**（`ScanOptions.ForceReparse`），
    /// 用来补旧版本入库、深字段为空的行。同样受手动扫描互斥约束（连点只跑一轮）。
    /// </summary>
    private Task ManualRescanAllAsync() => RunManualScanAsync(forceReparse: true, label: "Full rescan");

    private async Task RunManualScanAsync(bool forceReparse, string label)
    {
        if (_shuttingDown || _lifetimeCts.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref _manualScanRunning, 1, 0) == 1) return;

        try
        {
            _forceReparseForNextScan = forceReparse;   // 只作用于本次扫描，ScanIpasAsync 读完即复位
            AppendLog(forceReparse
                ? "Full rescan starting (all present files will be re-parsed; this may take a while)…"
                : "Manual refresh: starting incremental scan…", LogKind.Start);
            await RunScanTrackedAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppendLog(label + ": canceled", LogKind.End);
        }
        catch (Exception ex)
        {
            AppendLog(label + "Incomplete: " + ex.GetType().Name + ": " + ex.Message, LogKind.Error);
        }
        finally
        {
            // 用完立刻复位：下一次自动/快速扫描必须回到"Only resolve Pending/Failed"
            _forceReparseForNextScan = false;
            Volatile.Write(ref _manualScanRunning, 0);
        }
    }

    /// <summary>把库里的全部行并进网格（M4-b2）：按 `Packages.Id` 复用行对象、原地更新，不整表重建（硬约束 #8）。</summary>
    /// <summary>取消正在进行的索引预热（新一轮扫描开始 / 数据要变时调用）。</summary>
    private void CancelIndexPrefetch()
    {
        var cts = _indexPrefetchCts;
        _indexPrefetchCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* 取消失败不影响 */ }
        cts.Dispose();
    }

    private sealed record PrefetchResult(
        Dictionary<(LibrarySortField Field, bool Descending), int[]> Indexes, long Version, long ElapsedMs);

    /// <summary>
    /// 后台预热"Full field × both directions"的排序索引（不占 UI 线程；扫描完成且数据变化/无有效索引时调用）。
    /// 不新增启动图：主窗口立即显示，索引在后台建，建完点列头即走快路径。
    /// </summary>
    private void StartIndexPrefetch()
    {
        if (_shuttingDown) return;
        CancelIndexPrefetch();
        var (rows, version) = _libraryView.SnapshotForPrefetch();
        if (rows.Count == 0) return;

        var cts = new CancellationTokenSource();
        _indexPrefetchCts = cts;
        var token = cts.Token;
        var progress = new Progress<PrefetchResult>(result =>
        {
            if (token.IsCancellationRequested) return;
            _libraryView.ApplyPrefetch(result.Indexes, result.Version);
            AppendLog(
                $"Sort index prewarm complete: {LibraryView.PrefetchFieldCount} fields × 2 directions / {rows.Count} rows in {result.ElapsedMs} ms",
                LogKind.Detail);
        });

        _ = SafeFireAndForget.Run(() => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var indexes = LibraryView.BuildPrefetchIndexes(rows, token);
            sw.Stop();
            ((IProgress<PrefetchResult>)progress).Report(
                new PrefetchResult(indexes, version, (long)sw.Elapsed.TotalMilliseconds));
        }, token), "Prewarm sort index");
    }

    private void SyncLibrary(List<PackageRow> rows)
    {
        _rootDisplayDirty = true;   // 扫描可能带来新行/改路径：根显示要重新下发
        _libraryView.ReplaceAll(rows);
        _missingIcons.Clear();      // 扫描可能刚写出新图标 → 负缓存作废（进度 §5.2 第 8 条）
        NotifyMultiSelectionChanged();
        ReloadGroups(_libraryView.AllFiles.ToList());
        RefreshLibraryRows(force: true);
    }

    // ── 队列事件 → 界面（UI 线程） ────────────────────────────────────────────

    // 2026-11-07：回读完成后的空间预演日志（只提示、不改队列）。
    private readonly ConcurrentDictionary<string, byte> _spaceProjectionRunning = new();
    private readonly ConcurrentDictionary<string, (long Version, string Key)> _spaceWarnings = new(StringComparer.Ordinal);

    private void OnQueueEvent(QueueEvent e)
    {
        var device = Devices.FirstOrDefault(d =>
            string.Equals(d.Device.Udid, e.Udid, StringComparison.Ordinal));

        switch (e)
        {
            case JobStatusEvent j:
                if (j.Status == QueueStatus.Done)
                {
                    // 2026-11-02 用户定：队列**安装成功** → 记一条绿色（临时，只存内存、按设备分开记，刷新即清）
                    // 2026-11-03 审查 A4/B3 修复：**卸载成功要清掉该包绿**（含"Automatically uninstall after installation"的临时条目）
                    if (j.Kind == JobKind.Uninstall) _successMarks.Clear(j.Udid, j.BundleId);
                    else RecordInstallSuccess(j);
                }
                device?.Apply(j);
                device?.ReorderJobs();   // 2026-11-02：状态变化后按优先级重排（高优先级靠上）
                break;
            case JobProgressEvent p:
                device?.Apply(p);
                break;
            case JobInstallProgressEvent ip:
                device?.Apply(ip);
                break;
            case DeviceRunStateEvent r:
                device?.Apply(r);
                break;
            case JobAddedEvent added:
                device?.AddJob(added.Item);
                device?.ReorderJobs();
                break;
            case JobRemovedEvent removed:
                device?.RemoveJob(removed.JobId);
                break;
            case InstalledAppsEvent i:
                device?.Apply(i);
                _ = SafeFireAndForget.Run(() => RunSpaceProjectionAfterReadbackAsync(i.Udid), "Space precheck");
                if (device is not null && ReferenceEquals(device, SelectedDevice) && _isDeviceAppsMode)
                    _ = SafeFireAndForget.Run(RefreshDeviceAppsAsync, "Refresh device app");
                break;
        }

        // 行状态标记跟随队列/回读变化（2026-11-01）
        if (_isDeviceAppsMode && device is not null && ReferenceEquals(device, SelectedDevice)) RefreshDeviceAppsMarkers();
        if (device is not null && ReferenceEquals(device, SelectedDevice)) ScheduleRefreshLibraryMarkers();
    }

    /// <summary>
    /// 每次回读完成后做一次队列空间预演（2026-11-07 用户定）：只写日志提示，不改队列。
    /// 用队列版本 + 装不下任务集合去重；完全相同不重复写。
    /// </summary>
    private async Task RunSpaceProjectionAfterReadbackAsync(string udid)
    {
        if (_shuttingDown) return;
        if (!_spaceProjectionRunning.TryAdd(udid, 0)) return;

        try
        {
            // 强制安装明确跳过空间检查：不参与回读后的空间预演，也不产生“装不下”提示。
            var snapshot = _queue.Snapshot(udid).Where(x => !x.ForceInstall).ToList();
            var hasInstall = snapshot.Any(x => x.Kind == JobKind.Install &&
                                               !x.Status.IsFinished() &&
                                               !x.Status.IsInFlight());
            if (!hasInstall)
            {
                _spaceWarnings.TryRemove(udid, out _);
                return;
            }

            DiskUsage usage;
            try
            {
                usage = await _cli.QueryDiskUsageAsync(udid, CancellationToken.None).ConfigureAwait(true);
            }
            catch
            {
                return;
            }

            if (_shuttingDown) return;   // await 之后复核，避免关窗后写日志
            if (!usage.Ok)
            {
                _spaceWarnings.TryRemove(udid, out _);
                return;
            }

            var projection = QueueSpaceProjectionCalculator.Check(usage.FreeBytes, snapshot);
            if (projection.CannotFit.Count == 0)
            {
                _spaceWarnings.TryRemove(udid, out _);
                return;
            }

            var version = _queue.Version;
            var key = string.Join(",", projection.CannotFit
                .Select(x => x.Id.ToString("N"))
                .OrderBy(x => x, StringComparer.Ordinal));
            if (_spaceWarnings.TryGetValue(udid, out var last) && last.Version == version && last.Key == key)
                return;

            if (_shuttingDown) return;   // 关窗后不再写日志
            _spaceWarnings[udid] = (version, key);
            var queue = Devices.FirstOrDefault(d =>
                string.Equals(d.Device.Udid, udid, StringComparison.Ordinal));
            var names = string.Join("、", projection.CannotFit.Select(x =>
                x.Kind == JobKind.Install ? Path.GetFileName(x.FilePath ?? x.BundleId) : x.BundleId));
            AppendLog(
                $"Space precheck: {FormatSpace(usage.FreeBytes)} free; the following tasks do not fit in queue order: {names}." +
                "Free up space and try again. The queue was not changed.",
                LogKind.Error,
                queue?.Device);
        }
        catch (OperationCanceledException)
        {
            // 关窗/取消：正常收尾。
        }
        finally
        {
            _spaceProjectionRunning.TryRemove(udid, out _);
        }
    }

    private static string FormatSpace(long bytes)
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

    /// <summary>
    /// 记一条「这台设备装成功了这个包」（绿色提示的唯一来源，2026-11-02 用户定）：
    /// 身份 = BundleId + 入队版本（与 ✓ 的精确匹配同口径）；只有安装任务算，卸载成功不染绿。
    /// </summary>
    private void RecordInstallSuccess(JobStatusEvent e)
    {
        if (e.Kind == JobKind.Uninstall) return;

        // 审查 A4：自动卸载的临时条目不在队列里，不能只靠 Snapshot 查；事件里已带 BundleId/Kind
        var item = _queue.Snapshot(e.Udid).FirstOrDefault(x => x.Id == e.JobId);
        if (item is not null && item.Kind != JobKind.Install) return;

        var bundleId = item?.BundleId ?? e.BundleId;
        var version = item?.PackageVersion;
        _successMarks.Add(e.Udid, bundleId, version);
    }

    /// <summary>
    /// 重算资料库行状态（2026-11-01；2026-11-02 第二轮修订）：
    /// 行色 = 黄（该包在当前设备队列里）/ 红（在这台设备上安装失败）/ **绿（这台设备上安装成功，临时）** / 无；
    /// ✓ 列 = 设备上装着**同 BundleId + 同版本**（<see cref="InstalledMatcher"/>，刷新不清）。
    /// 组行**复用普通行接口**：状态 = 成员聚合（<see cref="LibraryView.ApplyGroupState"/>），
    /// 绿画在组行图标列、勾画在 ✓ 列；成员行无论组收起与否都要参与聚合（踩坑 C34）。
    /// 10k 行只是内存遍历，不做节流。
    /// </summary>
    /// <summary>合并入口：150 ms 内多次事件只算一次（与列表刷新节流同思路）。</summary>
    private void ScheduleRefreshLibraryMarkers()
    {
        if (_markersTimer is null)
        {
            _markersTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _markersTimer.Tick += (_, _) =>
            {
                _markersTimer!.Stop();
                RefreshLibraryMarkers();
            };
        }

        if (!_markersTimer.IsEnabled) _markersTimer.Start();
    }

    /// <param name="updateInstalled">
    /// 是否重算 ✓（默认 true）。「刷新」传 false —— 用户口径是"Clear mark, **Check to keep**"，
    /// 且回读未到时重算会把勾全清掉。
    /// </param>
    private void RefreshLibraryMarkers(bool updateInstalled = true)
    {
        var perfTotal = PerfProbe ? Stopwatch.StartNew() : null;
        var queueVm = SelectedDevice;
        if (queueVm is null || _libraryRows.Count == 0) return;

        var udid = queueVm.Device.Udid;
        var snapshot = _queue.Snapshot(udid);
        var installed = queueVm.InstalledApps;

        // C4（2026-11-04）：输入签名没变就直接跳过（进度事件只改百分比、不改状态 → 不再全表重算）。
        var markerSignature = MarkerSignature(udid, snapshot, installed, updateInstalled);
        if (string.Equals(markerSignature, _lastMarkerSignature, StringComparison.Ordinal) &&
            updateInstalled == _lastMarkerUpdateInstalled)
        {
            PerfProbeLog("markers skip");
            return;
        }

        _lastMarkerSignature = markerSignature;
        _lastMarkerUpdateInstalled = updateInstalled;

        // 同一 BundleId 只有一个队列行；操作状态按 (UDID, BundleId) 解析。
        var jobsByBundle = new Dictionary<string, QueueItem>(StringComparer.OrdinalIgnoreCase);
        var visualByBundle = new Dictionary<string, OperationVisual>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot)
        {
            if (string.IsNullOrWhiteSpace(item.BundleId)) continue;
            jobsByBundle[item.BundleId] = item;
            visualByBundle[item.BundleId] = OperationVisuals.Resolve(item.Kind, item.Status);
        }

        // 安装任务按“包身份”（名称 + BundleId + 版本 + 大小）合并；用任务路径找到身份后，
        // 把同身份的所有包行一起染色，而不是只染路径命中的那一行。
        var identityVisual = new Dictionary<string, OperationVisual>(StringComparer.Ordinal);
        foreach (var row in _libraryView.AllFiles)
        {
            if (!jobsByBundle.TryGetValue(row.BundleId, out var item) || item.Kind != JobKind.Install) continue;
            if (string.IsNullOrWhiteSpace(item.PackageKey)) continue;
            if (!string.Equals(item.PackageKey, row.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
            identityVisual[PackageIdentity(row)] = visualByBundle[row.BundleId];
        }

        // 普通行：**遍历全部文件行**（含被搜索过滤 / 组收起后不在网格里的成员）。
        foreach (var row in _libraryView.AllFiles)
        {
            if (updateInstalled)
            {
                row.InstalledState = InstalledMatcher.Classify(
                    installed, row.BundleId, row.PackageVersionForQueue, row.Name) switch
                {
                    InstalledMatchKind.SameVersion => LibraryInstalledState.Yes,
                    InstalledMatchKind.OtherVersion => LibraryInstalledState.OtherVersion,
                    _ => LibraryInstalledState.None,
                };
            }

            var visual = OperationVisual.None;
            if (jobsByBundle.TryGetValue(row.BundleId, out var item))
            {
                var candidate = visualByBundle[row.BundleId];
                if (candidate != OperationVisual.None)
                {
                    if (item.Kind == JobKind.Install)
                    {
                        // 安装：优先精确路径；否则按包身份共享颜色
                        if (string.Equals(item.PackageKey, row.FilePath, StringComparison.OrdinalIgnoreCase) ||
                            identityVisual.ContainsKey(PackageIdentity(row)))
                            visual = candidate;
                    }
                    else if (item.Status == QueueStatus.Done)
                    {
                        // 卸载完成：设备回读已没有该 App，按 BundleId 把资料库里的行标成功；
                        // 若回读仍能匹配到实际安装版本，则只标匹配行（下面分支覆盖）。
                        visual = candidate;
                    }
                    else
                    {
                        // 卸载中/失败：只给当前设备实际安装版本的行染色（名称 + BundleId + 版本，大小不参与）。
                        if (InstalledMatcher.Classify(
                                installed, row.BundleId, row.PackageVersionForQueue, row.Name) == InstalledMatchKind.SameVersion)
                            visual = candidate;
                    }
                }
            }

            // 本次安装成功（队列已清但内存成功标记还在）仍然整行绿。
            if (visual == OperationVisual.None &&
                _successMarks.Contains(udid, row.BundleId, row.PackageVersionForQueue))
                visual = OperationVisual.Succeeded;

            row.Visual = visual;
            row.Marker = visual switch
            {
                OperationVisual.Failed => LibraryMarker.Failed,
                OperationVisual.Succeeded => LibraryMarker.Installed,
                OperationVisual.None => LibraryMarker.None,
                _ => LibraryMarker.Queued,
            };
        }

        // 组行：勾列 = 任一成员匹配已安装；icon 列只按匹配已安装版本的成员取操作状态。
        foreach (var row in _libraryRows)
        {
            if (!row.IsGroup) continue;
            var members = _libraryView.GroupMembers(row.GroupBundleId);
            LibraryView.ApplyGroupState(row, members);

            row.Visual = OperationVisual.None;
            var hasInstalledMember = members.Any(m => m.InstalledState == LibraryInstalledState.Yes);
            row.GroupVisual = hasInstalledMember &&
                              visualByBundle.TryGetValue(row.GroupBundleId, out var groupVisual)
                ? groupVisual
                : OperationVisual.None;
        }

        perfTotal?.Stop();
        PerfProbeLog($"markers rows={_libraryRows.Count} ms={(perfTotal?.Elapsed.TotalMilliseconds ?? 0):F2}");
    }

    /// <summary>资料库包身份：名称 + BundleId + 版本 + 大小（2026-11-06 用户定）。</summary>
    private static string PackageIdentity(LibraryRow row)
        => row.Name + "\u001F" + row.BundleId + "\u001F" + row.PackageVersionForQueue + "\u001F"
           + row.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 标记重算输入签名（C4）：设备 + 队列状态/Kind/Key + 已装列表引用 + 绿色版本 + 行数据版本 + 是否重算勾。
    /// 只用于"This round is exactly the same as the last round, so skip it."，不决定具体行状态（状态仍由下面全量重算得出）。
    /// </summary>
    private string MarkerSignature(
        string udid, IReadOnlyList<QueueItem> snapshot, IReadOnlyList<InstalledApp>? installed, bool updateInstalled)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(udid).Append('|').Append(updateInstalled ? '1' : '0').Append('|')
            .Append(_libraryView.DataVersion).Append('|').Append(_successMarks.Version).Append('|');
        foreach (var item in snapshot)
        {
            sb.Append(item.Id).Append(':').Append((int)item.Status).Append(':').Append((int)item.Kind)
                .Append(':').Append(item.PackageKey).Append(':').Append(item.BundleId).Append(';');
        }

        sb.Append('|').Append(installed is null ? "null" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(installed).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(',').Append(installed?.Count ?? 0);
        return sb.ToString();
    }

    // ── 设置（2026-11-02 用户定：设置窗口，docs/01 §2.4 / docs/09 §3.2）──────────

    /// <summary>设置窗口是否已打开（防重复开窗）。</summary>
    public bool TryBeginOpenSettings()
    {
        if (_settingsWindowOpen) return false;
        _settingsWindowOpen = true;
        return true;
    }

    public void EndOpenSettings() => _settingsWindowOpen = false;

    /// <summary>窗口用的设置快照（布局两项由窗口从 MainWindow 补上）。</summary>
    public SettingsDraft CaptureSettings() => new()
    {
        RowHeight = _libraryRowHeight,
        Sort = new LibrarySort(_libraryView.Sort.Field, _libraryView.Sort.Descending),
        Scope = SettingsDraft.CloneScope(_searchScope),
        MatchMode = _searchMatchMode,
        SearchDebounceRows = _searchDebounceRowThreshold,
        SearchDebounceMs = (int)_searchDebounce.TotalMilliseconds,
        DeviceFamilyRetinaText = _deviceFamilyRetinaText,
        MergeSameBundle = _libraryView.MergeSameBundle,
        ShowMissingFiles = _showMissingFiles,
        ScanBatchSize = _scanBatchSize,
        IconCacheMaxMb = _iconCacheMaxMb,
        IconMatchDisplayScale = _iconMatchDisplayScale,
        IconDownscaleCache = _iconDownscaleCache,
        IconBucketRound = 16,
        LogToFile = _logToFile,
        LogDetailMode = _logMode == LogMode.Detail,
        LogHidden = false,          // 由设置窗口用 MainWindow 当前值覆盖
        LogOverlayWidth = 420,      // 同上
        PollSeconds = _devicePollSeconds,
        AutoResume = _autoResume,
        ReconnectStableSeconds = _reconnectStableSeconds,
        AutoRetryAttempts = _autoRetryAttempts,
        DeviceFields = DeviceInfo.Fields.ToList(),
        QueueColumns = _queueColumnSettings.ToList(),
    };

    /// <summary>设置窗口显示用：排序/搜索范围显示“已保存的默认值”，其余仍是当前状态。</summary>
    public SettingsDraft CaptureDefaultSettings()
    {
        var draft = CaptureSettings();
        draft.Sort = new LibrarySort(_defaultSort.Field, _defaultSort.Descending);
        draft.Scope = SettingsDraft.CloneScope(_defaultScope);
        return draft;
    }

    /// <summary>当前设备轮询秒数（设置窗口读）。</summary>
    public int DevicePollSeconds => _devicePollSeconds;

    /// <summary>
    /// 应用设置（2026-11-02 用户定）：`persist=false` 只做**预览**，`true` 同时写库。
    /// 布局两项（日志显示/浮层宽度）由设置窗口直接作用到 MainWindow，不在这里处理。
    /// </summary>
    public void ApplySettings(SettingsDraft draft, bool persist)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // ① 行高（图标尺寸=行高，视图那边跟着变）
        // 注意：设置窗口是"Preview first (persist=false) and then apply (persist=true)"，预览已经改过内存值；
        // 因此 persist 时必须**无条件写键**，不能只在"different from memory"时写（否则预览过的值永远落不了库）。
        var rowHeight = LibraryPrefs.ClampRowHeight(draft.RowHeight);
        if (Math.Abs(_libraryRowHeight - rowHeight) > 0.01)
        {
            _libraryRowHeight = rowHeight;
            OnPropertyChanged(nameof(LibraryRowHeight));
        }
        if (persist) SaveSetting(LibraryPrefs.RowHeightSettingKey, LibraryPrefs.FormatRowHeight(rowHeight));

        // ② 排序：persist 时更新“默认排序”，当前视图同步到该值；预览只改当前视图。
        if (persist)
        {
            _defaultSort = new LibrarySort(draft.Sort.Field, draft.Sort.Descending);
            SaveSetting(LibraryPrefs.SortSettingKey, LibraryPrefs.FormatSort(_defaultSort));
        }
        if (draft.Sort != _libraryView.Sort)
        {
            _libraryView.Sort = new LibrarySort(draft.Sort.Field, draft.Sort.Descending);
            OnPropertyChanged(nameof(LibrarySort));
            RefreshLibraryRows(force: true);
        }

        // ③ 搜索范围 / 匹配方式
        _searchScope.CopyFrom(draft.Scope);
        if (persist)
        {
            _defaultScope = SettingsDraft.CloneScope(_searchScope);
            SaveSetting(LibraryPrefs.SearchScopeSettingKey, _defaultScope.Format());
        }
        if (_searchMatchMode != draft.MatchMode)
        {
            _searchMatchMode = draft.MatchMode;
            OnPropertyChanged(nameof(SearchMatchMode));
            OnPropertyChanged(nameof(SearchMatchModeText));
        }
        _libraryView.Scope = _searchScope;
        _libraryView.MatchMode = _searchMatchMode;
        if (persist)
        {
            // 搜索范围默认值已在上面保存；匹配方式沿用原逻辑
            SaveSetting(LibraryPrefs.SearchModeSettingKey, LibraryPrefs.FormatSearchMode(_searchMatchMode));
        }

        // ③b 搜索防抖（2026-11-04）：行数阈值 / 延迟 ms；预览只改内存，persist 才写库
        var debounceRows = LibraryPrefs.ClampSearchDebounceRows(draft.SearchDebounceRows);
        var debounceMs = LibraryPrefs.ClampSearchDebounceMs(draft.SearchDebounceMs);
        if (_searchDebounceRowThreshold != debounceRows)
            _searchDebounceRowThreshold = debounceRows;
        if ((int)_searchDebounce.TotalMilliseconds != debounceMs)
            _searchDebounce = TimeSpan.FromMilliseconds(debounceMs);
        if (persist)
        {
            // 预览已改过内存值 → persist 时无条件写键（同 ① 行高）
            SaveSetting(LibraryPrefs.SearchDebounceRowsSettingKey,
                debounceRows.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SaveSetting(LibraryPrefs.SearchDebounceMsSettingKey,
                debounceMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // ③c 设备族 Retina/R 文案（2026-11-05）：预览即改内存并重算，persist 才写库
        if (_deviceFamilyRetinaText != draft.DeviceFamilyRetinaText)
        {
            _deviceFamilyRetinaText = draft.DeviceFamilyRetinaText;
            _libraryView.ReloadDeviceFamilyText(_deviceFamilyRetinaText);
            RefreshLibraryRows(force: true);
        }
        if (persist) SaveSetting(LibraryPrefs.DeviceFamilyRetinaSettingKey,
            _deviceFamilyRetinaText ? "true" : "false");

        // ④ 合并 / 显示缺失 / 兼容过滤（2026-11-02 起持久化）
        LibraryMergeSameBundle = draft.MergeSameBundle;
        ShowMissingFiles = draft.ShowMissingFiles;
        if (persist)
        {
            SaveSetting("Grid.MergeSameBundle", draft.MergeSameBundle ? "true" : "false");
            SaveSetting("Grid.ShowMissingFiles", draft.ShowMissingFiles ? "true" : "false");
        }

        // ⑤ 扫描 / 图标（要重建扫描器；只改实际变化的值，避免预览时反复重建）
        var scanChanged = false;
        var batch = Math.Clamp(draft.ScanBatchSize, 1, ScanOptions.MaxBatchSize);
        if (_scanBatchSize != batch) { _scanBatchSize = batch; scanChanged = true; }
        if (persist) SaveSetting(ScanOptions.BatchSizeKey, batch.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var iconChanged = false;
        var cacheMax = Math.Clamp(draft.IconCacheMaxMb, 64, 20480);
        if (_iconCacheMaxMb != cacheMax) { _iconCacheMaxMb = cacheMax; iconChanged = true; }
        if (_iconMatchDisplayScale != draft.IconMatchDisplayScale) { _iconMatchDisplayScale = draft.IconMatchDisplayScale; iconChanged = true; }
        if (_iconDownscaleCache != draft.IconDownscaleCache) { _iconDownscaleCache = draft.IconDownscaleCache; iconChanged = true; }
        if (persist)
        {
            SaveSetting(IconCacheOptions.CacheMaxMbKey, cacheMax.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SaveSetting(IconCacheOptions.MatchDisplayScaleKey, _iconMatchDisplayScale ? "true" : "false");
            SaveSetting(IconCacheOptions.DownscaleCacheKey, _iconDownscaleCache ? "true" : "false");
        }
        if ((scanChanged || iconChanged) && _db is not null) CreateScanner(_db);

        // ⑥ 日志
        ApplyLogToFile(draft.LogToFile, persist);
        SetLogPanelMode(draft.LogDetailMode ? LogMode.Detail : LogMode.Simple, persist);

        // ⑦ 设备：轮询间隔 / 断连自动继续
        var pollSeconds = Math.Clamp(draft.PollSeconds, 1, 10);
        if (_devicePollSeconds != pollSeconds)
        {
            _devicePollSeconds = pollSeconds;
            if (_devicePollTimer is not null) _devicePollTimer.Interval = TimeSpan.FromSeconds(pollSeconds);
            if (persist) SaveSetting("Device.PollSeconds", pollSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (_autoResume != draft.AutoResume)
        {
            _autoResume = draft.AutoResume;
            if (persist) SaveSetting("Device.AutoResumeAfterReconnect", _autoResume ? "true" : "false");
        }

        var stableSeconds = Math.Clamp(draft.ReconnectStableSeconds, 1, 10);
        if (_reconnectStableSeconds != stableSeconds)
        {
            _reconnectStableSeconds = stableSeconds;
            if (persist) SaveSetting("Device.ReconnectStableSeconds", stableSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var retryAttempts = Math.Clamp(draft.AutoRetryAttempts, 2, 5);
        if (_autoRetryAttempts != retryAttempts)
        {
            _autoRetryAttempts = retryAttempts;
            _queue.AutoRetryAttempts = retryAttempts;
            if (persist) SaveSetting("Queue.AutoRetryAttempts", retryAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // ⑧ 设备信息字段（顺序 + 可见性）
        if (!DeviceInfo.Fields.SequenceEqual(draft.DeviceFields))
        {
            DeviceInfo.Fields = draft.DeviceFields.ToList();
            foreach (var device in Devices) device.Device.RefreshInfoLine();
            if (persist) SaveSetting(DeviceInfoFields.SettingsKey, DeviceInfoFields.Serialize(DeviceInfo.Fields));
        }

        // ⑨ 队列行字段（顺序 + 可见性；Display 字符串重新拼）
        if (!_queueColumnSettings.SequenceEqual(draft.QueueColumns))
        {
            _queueColumnSettings = draft.QueueColumns.ToList();
            ApplyQueueColumnsToDevices();
        }
        if (persist) SaveSetting(QueueColumns.SettingsKey, QueueColumns.Serialize(_queueColumnSettings));
    }

    private void ApplyLogToFile(bool enabled, bool persist)
    {
        if (_logToFile == enabled) return;

        _logToFile = enabled;
        try { _fileLog?.Dispose(); } catch (ObjectDisposedException) { }
        _fileLog = new FileLog(AppPaths.LogsDirectory, enabled);
        AppendLog(enabled ? "File log enabled: logs\app.log" : "File log disabled (crash.log is still written)", LogKind.Detail);
        if (persist) SaveSetting(SettingsStore.LogToFile, enabled ? "true" : "false");
    }

    private void SetLogPanelMode(LogMode mode, bool persist)
    {
        if (_logMode == mode) return;

        _logMode = mode;
        OnPropertyChanged(nameof(LogModeButtonText));
        RebuildLogLines();
        if (persist) SaveSetting(SettingsStore.LogPanelMode, LogFilter.ToSettingValue(mode));
    }

    /// <summary>设置窗口「扫描」分组的根列表：显示名 + 该根当前的包数（只统计库里的行）。</summary>
    public IReadOnlyList<(ScanRootRow Row, string Label, int Packages)> ScanRootEntries()
    {
        var counts = new Dictionary<long, int>();
        foreach (var row in _libraryView.AllFiles)
        {
            if (row.ScanRootId == 0) continue;
            counts[row.ScanRootId] = counts.GetValueOrDefault(row.ScanRootId) + 1;
        }

        var list = new List<(ScanRootRow, string, int)>(_scanRoots.Count);
        foreach (var root in _scanRoots)
            list.Add((root, LabelFor(root), counts.GetValueOrDefault(root.Id)));
        return list;
    }

    /// <summary>添加扫描根（设置窗口用）：写入 + 重新编号 + 立刻增量扫描。</summary>
    public async Task<(bool Ok, string? Error)> AddScanRootAsync(string path)
    {
        if (_db is null || _scanner is null) return (false, "Database not ready");

        try
        {
            var (row, error) = await Task.Run(() => ScanRootStore.Add(_db, path));
            if (row is null) return (false, error ?? "Unknown error");

            _scanRoots.Clear();
            var rows = await Task.Run(() => ScanRootStore.List(_db));
            _scanRoots.AddRange(rows.Where(r => r.Enabled));
            _rootNumbers = await Task.Run(() => ScanRootNumbers.Ensure(_db, _scanRoots));
            RebuildRootDisplay();

            var label = LabelFor(row);
            AppendLog($"Added scan directory {label}: {row.Path}", LogKind.End);
            ReportStatus($"[{row.Path}] added to scan directories ({label}); starting scan…");
            ApplySettingsPreviewRefresh();
            await ScanAfterRootChangeAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>删除扫描根（设置窗口用）：删根 + **删该根下的包行**（避免库膨胀）+ 立刻增量扫描。</summary>
    public async Task<(bool Ok, string? Error)> DeleteScanRootAsync(ScanRootRow root)
    {
        if (_db is null || _scanner is null) return (false, "Database not ready");

        try
        {
            var removed = await Task.Run(() =>
            {
                var count = ScanRootStore.Delete(_db, root.Id);
                ScanRootNumbers.Remove(_db, root.Path);
                return count;
            });

            // 重读根列表：**删到 0 个根也不补默认目录**（删完就是没有根）
            _scanRoots.Clear();
            var rows = await Task.Run(() => ScanRootStore.List(_db));
            _scanRoots.AddRange(rows.Where(r => r.Enabled));
            _rootNumbers = await Task.Run(() => ScanRootNumbers.Ensure(_db, _scanRoots));
            RebuildRootDisplay();
            ApplySettingsPreviewRefresh();

            AppendLog($"Removed scan directory {root.Path} ({removed} package rows under it were removed); starting incremental scan…", LogKind.End);
            ReportStatus($"[{root.Path}] scan directory removed ({removed} packages removed); scanning incrementally…");
            await ScanAfterRootChangeAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 扫描根增删后的扫描入口（审查 N3，2026-11-03）：先等正在跑的扫描收尾（避免"Just deleted the root, the old scan is still writing the root"→ FK 违约），
    /// 再走与手动刷新同一套互斥 + <see cref="RunScanTrackedAsync"/>（登记关窗链）。
    /// </summary>
    private async Task ScanAfterRootChangeAsync()
    {
        // 等当前扫描收尾（最多等 30 s；关窗/取消时直接退出）
        var deadline = Environment.TickCount64 + 30_000;
        while (!_shuttingDown && Environment.TickCount64 < deadline)
        {
            var running = _activeScanTask;
            if (running is null || running.IsCompleted) break;
            try { await running.ConfigureAwait(true); } catch { /* 旧扫描失败不影响本次 */ }
        }

        if (_shuttingDown || _lifetimeCts.IsCancellationRequested) return;

        // 与手动刷新互斥（连点只跑一轮）
        while (Interlocked.CompareExchange(ref _manualScanRunning, 1, 0) == 1)
        {
            if (_shuttingDown) return;
            await Task.Delay(50).ConfigureAwait(true);
        }

        try
        {
            await RunScanTrackedAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Scan after scan root change canceled", LogKind.End);
        }
        catch (Exception ex)
        {
            AppendLog("Scan after scan root change incomplete: " + ex.GetType().Name + ": " + ex.Message, LogKind.Error);
        }
        finally
        {
            Volatile.Write(ref _manualScanRunning, 0);
        }
    }

    /// <summary>扫描根变化后把"Path column display"与列表刷新一次（根增删是动作、不进设置草稿）。</summary>
    private void ApplySettingsPreviewRefresh()
    {
        RefreshLibraryRows(force: true);
    }

    /// <summary>重算根显示缓存（Id → 路径 + RootN）并下发到行。</summary>
    private void RebuildRootDisplay()
    {
        _rootDisplayDirty = true;
        _rootDisplay.Clear();
        foreach (var root in _scanRoots)
        {
            var label = LabelFor(root);
            _rootDisplay[root.Id] = (root.Path, label);
        }

        ApplyRootDisplayToRows();
    }

    private void ApplyRootDisplayToRows()
    {
        // C2：根映射/扫描数据没变时不再全表重设（50k 每次刷新省 2–5 ms）
        if (!_rootDisplayDirty) return;
        _rootDisplayDirty = false;
        foreach (var row in _libraryView.AllFiles)
        {
            if (row.ScanRootId != 0 && _rootDisplay.TryGetValue(row.ScanRootId, out var info))
            {
                row.RootPath = info.Path;
                row.RootLabel = info.Label;
            }
            else
            {
                row.RootPath = "";
                row.RootLabel = "";
            }
        }
    }

    private string LabelFor(ScanRootRow root)
    {
        var key = ScanRootNumbers.Normalize(root.Path);
        return _rootNumbers.TryGetValue(key, out var number) ? ScanRootNumbers.Label(number) : "Root?";
    }

    // ── IPA ───────────────────────────────────────────────────────────────────

    private async Task AddIpaAsync()
    {
        var files = _pickIpaFiles();
        if (files is null || files.Length == 0) return;

        var todo = files.Where(f => !_libraryView.ContainsPath(f)).ToList();
        if (todo.Count == 0) return;

        // 仍是浅解析（进度 §5.1 1.1 的 open 债）：没有体积 / 图标 / 架构，先按"Not in stock"临时行显示
        var parsed = await Task.Run(() => todo.Select(IpaReader.Read).ToList());
        foreach (var info in parsed) _libraryView.AddTransient(info, _nextSyntheticId--);
        RefreshLibraryRows(force: true);
        SelectedLibraryRow = LibraryRows.FirstOrDefault(r => !r.IsGroup);
    }

    // ── 设备 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 原地更新：同一 UDID 只更新已有对象，绝不 Devices.Clear() 重建。
    /// 旧实现会在刷新时丢掉正在跑的泵，导致后台还在装、界面队列消失、还能再起一个泵双装
    /// （进度 §5 第 5 条）。
    /// <para>
    /// 2026-11-02 修 bug：L3 实测到一次 `刷新设备失败：Collection was modified`——
    /// 按钮刷新与 2 s 轮询会**并发**进入本方法，两个循环跨 `await` 同时改 `Devices`（Add / Move / 置离线）。
    /// 加互斥：同一时刻只允许一轮设备刷新，后进来的直接返回（下一轮轮询会补上）。
    /// </para>
    /// </summary>
    private async Task RefreshDevicesAsync()
    {
        if (Interlocked.CompareExchange(ref _deviceRefreshing, 1, 0) != 0)
        {
            // 审查 A5：被丢弃的刷新不能无声无息——记一个待补跑标记，当前这轮结束后补一次
            Interlocked.Exchange(ref _deviceRefreshPending, 1);
            return;
        }
        try
        {
            var udids = await _cli.ListUdidsAsync(CancellationToken.None);
            var seen = new HashSet<string>(udids, StringComparer.Ordinal);

            foreach (var udid in udids)
            {
                var probe = await _cli.ProbeAsync(udid, CancellationToken.None);
                if (!probe.Present)
                {
                    _deviceProbeFailStreak++;   // 在列表里但探测失败 → 计一次（审查 A5 退避用）
                    continue;
                }

                _deviceProbeFailStreak = 0;

                var existing = Devices.FirstOrDefault(d =>
                    string.Equals(d.Device.Udid, udid, StringComparison.Ordinal));

                if (existing is null)
                {
                    var device = new DeviceInfo
                    {
                        Udid = udid,
                        Name = string.IsNullOrEmpty(probe.Name) ? "Unknown device" : probe.Name,
                        Product = probe.ProductType,
                        Version = probe.Version,
                        CpuArch = probe.CpuArch,
                        Online = true,
                    };
                    var queueVm = new DeviceQueueViewModel(_queue, device);
                    queueVm.ApplyQueueColumns(QueueColumns.OrderedVisible(_queueColumnSettings));
                    queueVm.Device.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(DeviceInfo.Online)) OnDeviceOnlineChanged(queueVm);
                    };
                    Devices.Add(queueVm);
                    _queue.SetDeviceIdentity(udid, probe.ProductType, probe.Version, probe.Name);
                    ApplyProbe(device, probe);
                    _ = SafeFireAndForget.Run(() => RefreshDeviceDetailAsync(device), "Read device details");
                }
                else
                {
                    // 原地改属性：正在跑的泵、Jobs 集合、选中项都保留
                    existing.Device.Name = string.IsNullOrEmpty(probe.Name) ? existing.Device.Name : probe.Name;
                    existing.Device.Product = probe.ProductType;
                    existing.Device.Version = probe.Version;
                    existing.Device.CpuArch = probe.CpuArch;
                    existing.MarkOnline();   // 2026-11-02：插回时把 Offline 复位 Idle（否则一直显示"Device offline"）
                    _queue.SetDeviceIdentity(udid, probe.ProductType, probe.Version, probe.Name);
                    ApplyProbe(existing.Device, probe);
                    _ = SafeFireAndForget.Run(() => RefreshDeviceDetailAsync(existing.Device), "Read device details");
                }
            }

            // 不在本次列表里的设备：整行置离线，保留最后数据（02 §2）
            foreach (var stale in Devices.Where(d => !seen.Contains(d.Device.Udid)))
            {
                stale.MarkOffline();
            }

            ArrangeDevices();

            if (SelectedDevice is null || !SelectedDevice.Device.Online)
                SelectedDevice = Devices.FirstOrDefault(d => d.Device.Online) ?? Devices.FirstOrDefault();

            var onlineCount = Devices.Count(d => d.Device.Online);
            // 审查 A5：0 台在线常出现在"Device is in the list but detection failed"的抖动期，别用 End 级刷屏（简洁模式可见）
            AppendLog($"Device refresh complete: {onlineCount} online / {Devices.Count} total",
                onlineCount == 0 ? LogKind.Detail : LogKind.End);

            // 探测失败退避（审查 A5）：连续失败时把轮询间隔拉长，恢复后复位
            if (_devicePollTimer is not null)
            {
                _devicePollTimer.Interval = _deviceProbeFailStreak > 0
                    ? TimeSpan.FromSeconds(Math.Min(_devicePollSeconds * Math.Max(2, _deviceProbeFailStreak), 30))
                    : TimeSpan.FromSeconds(_devicePollSeconds);
            }
            ReportStatus($"Device refresh complete: {onlineCount} online / {Devices.Count} total");   // 成功即清掉旧红字（审核 P2-6）
            await PersistDevicesAsync();

            // 刷新设备后回读已安装集合（07 §3.5 / 06 §3.4 的 InstalledBundleIds）；后台做，不挡 UI
            foreach (var device in Devices.Where(d => d.Device.Online).ToList())
            {
                _ = SafeFireAndForget.Run(() => PrefetchInstalledAsync(device.Device.Udid), "Read back installed apps");
            }
        }
        catch (Exception ex)
        {
            AppendLog("Failed to refresh devices: " + ex.Message, LogKind.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _deviceRefreshing, 0);

            // 被丢弃的那次刷新现在补跑（只补一次；期间可能又有新请求，flag 会再被置起）
            if (Interlocked.Exchange(ref _deviceRefreshPending, 0) == 1 && !_shuttingDown)
            {
                _ = SafeFireAndForget.Run(RefreshDevicesAsync, "Retry device refresh");
            }
        }
    }

    /// <summary>
    /// 设备在线轮询（2026-11-02 用户定）：每 2 s 跑一次 `idevice_id -l`（实测 12–33 ms），
    /// **只在在线集合发生变化时**触发一次完整刷新（探测 + 置离线 + 重排 + 选焦点）。
    /// 修 bug：之前没有轮询，设备离线后插回必须手动点「刷新」才恢复在线。
    /// </summary>
    private void StartDevicePolling()
    {
        if (_devicePollTimer is not null) return;
        _devicePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_devicePollSeconds) };
        _devicePollTimer.Tick += (_, _) => _ = PollDevicesAsync();
        _devicePollTimer.Start();
    }

    private async Task PollDevicesAsync()
    {
        if (_shuttingDown) return;
        if (Interlocked.CompareExchange(ref _devicePolling, 1, 0) != 0) return;

        try
        {
            var udids = await _cli.ListUdidsAsync(CancellationToken.None);
            var seen = new HashSet<string>(udids, StringComparer.Ordinal);
            var online = Devices.Where(d => d.Device.Online)
                .Select(d => d.Device.Udid)
                .ToHashSet(StringComparer.Ordinal);

            // 插回（seen 有、我们标离线）/ 拔掉（我们标在线、seen 没有）/ 全新设备
            if (!online.SetEquals(seen) || udids.Any(u => Devices.All(d => d.Device.Udid != u)))
            {
                await RefreshDevicesAsync();
            }
            else if (_deviceProbeFailStreak > 0)
            {
                // 名单一致 = 探测抖动恢复：复位退避（审查 A5）
                _deviceProbeFailStreak = 0;
                if (_devicePollTimer is not null)
                    _devicePollTimer.Interval = TimeSpan.FromSeconds(_devicePollSeconds);
            }
        }
        catch (Exception)
        {
            // 轮询失败静默（不打断界面、不刷屏日志）
        }
        finally
        {
            Interlocked.Exchange(ref _devicePolling, 0);
        }
    }

    /// <summary>在线设备排前面、离线的排最后（2026-11-02 用户定：离线 tab 置灰并自动置后）。</summary>
    private void ArrangeDevices() => DeviceOrder.Arrange(Devices);

    /// <summary>
    /// 任何来源的在线状态变化（轮询刷新 / 队列失败判离线）都要重排 + 调焦点：
    /// 之前只在「刷新设备」末尾排一次，队列泵自己判离线时 tab 不会移动（2026-11-02 用户报的截图）。
    /// </summary>
    private void OnDeviceOnlineChanged(DeviceQueueViewModel queueVm)
    {
        ArrangeDevices();

        if (queueVm.Device.Online)
        {
            if (SelectedDevice is null) SelectedDevice = queueVm;
            if (_autoResume) ScheduleReconnectResume(queueVm);
            return;
        }

        CancelReconnectResume(queueVm.Device.Udid);
        if (ReferenceEquals(SelectedDevice, queueVm))
            SelectedDevice = Devices.FirstOrDefault(d => d.Device.Online) ?? Devices.FirstOrDefault();
    }

    /// <summary>
    /// 联机稳定窗口（2026-11-07 用户定）：设备重新在线后等待配置秒数；期间再次断开则重新计时。
    /// 稳定后只恢复「因断开挂起」与中断 Paused；用户手动挂起的保持。
    /// </summary>
    private void ScheduleReconnectResume(DeviceQueueViewModel queueVm)
    {
        var udid = queueVm.Device.Udid;
        CancelReconnectResume(udid);
        var cts = new CancellationTokenSource();
        _reconnectTimers[udid] = cts;

        _ = SafeFireAndForget.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_reconnectStableSeconds), cts.Token).ConfigureAwait(true);
                if (!queueVm.Device.Online) return;

                var restored = _queue.ResumeAfterReconnect(udid);
                if (restored > 0)
                {
                    AppendLog($"Online stable for {_reconnectStableSeconds} s; resumed {restored} tasks suspended by the disconnect: {queueVm.Device.ShortName}",
                        LogKind.End);
                    ReportStatus($"[{queueVm.Device.ShortName}] online and stable; {restored} tasks resumed");
                }
            }
            finally
            {
                if (_reconnectTimers.TryGetValue(udid, out var tracked) && ReferenceEquals(tracked, cts))
                    _reconnectTimers.TryRemove(udid, out _);
                cts.Dispose();
            }
        }, "Back online");
    }

    private void CancelReconnectResume(string udid)
    {
        if (_reconnectTimers.TryRemove(udid, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    /// <summary>M4-b4：把探测到的身份字段贴到设备上（Build / 颜色 / ECID / 序列号 / 部件号 / 板型）。</summary>
    private static void ApplyProbe(DeviceInfo device, DeviceProbe probe)
    {
        device.Build = probe.Build;
        device.Color = probe.Color;
        device.Ecid = probe.Ecid;
        device.Serial = probe.Serial;
        device.ModelNumber = probe.ModelNumber;
        device.HardwareModel = probe.HardwareModel;
    }

    /// <summary>
    /// M4-b4：每台设备的"slow information"（容量 + 电池三条）后台补，不挡设备列表与扫描；
    /// 同一台设备同时只跑一份（<see cref="_detailRefreshing"/>）。
    /// </summary>
    private async Task RefreshDeviceDetailAsync(DeviceInfo device)
    {
        if (_shuttingDown) return;          // 审查 P2-2：关窗后不再起新的 CLI 任务
        if (!_detailRefreshing.TryAdd(device.Udid, 0)) return;

        try
        {
            var udid = device.Udid;
            var space = await _cli.QueryDiskUsageAsync(udid, CancellationToken.None);
            var usage = await _cli.ReadBatteryUsageAsync(udid, CancellationToken.None);
            var health = await _cli.ReadBatteryHealthAsync(udid, CancellationToken.None);
            var spec = await _cli.ReadBatterySpecAsync(udid, CancellationToken.None);

            if (!device.Online) return;   // 中途断电：丢弃这次结果，下次刷新再补

            device.SetSpace(space);
            device.SetBattery(usage, health, spec);
            AppendLog(
                $"Device info: {device.ShortName} capacity {(space.Ok ? device.SpaceText : "未知")}、" +
                $"battery {(usage.HasValue ? usage.Display : "未知")}、" +
                $"health {(health.HasValue ? device.BatteryHealthText : "未知")}",
                LogKind.Detail);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to read device info ({device.Name}): {ex.Message}", LogKind.Error);
        }
        finally
        {
            _detailRefreshing.TryRemove(device.Udid, out _);
        }
    }

    /// <summary>设备最后快照写库（docs/09 §3.1：M2 设备刷新写 Devices）。失败只记日志，不影响队列。</summary>
    private async Task PersistDevicesAsync()
    {
        if (_db is null) return;
        try
        {
            var snapshot = Devices.Select(d => new DeviceRow
            {
                Udid = d.Device.Udid,
                Name = d.Device.Name,
                ProductType = d.Device.Product,
                IosVersion = d.Device.Version,
                CpuArch = d.Device.CpuArch,
                LastSeenUtc = UtcText.Now(),
            }).ToList();

            await Task.Run(() =>
            {
                // 一次事务写完 + 短上限（2 s）：设备快照不是关键数据，库被外部锁住时不该把启动拖住
                // （实测逐台 upsert 会每台各等满 busy_timeout，3 台约 33 s）。
                DeviceStore.UpsertMany(_db, snapshot, waitMs: 2000, busyTimeoutMs: 2000);
            });
        }
        catch (Exception ex)
        {
            AppendLog("Failed to write device table: " + ex.GetType().Name + ": " + ex.Message, LogKind.Error);
        }
    }

    /// <summary>后台回读某设备已安装集合，结果经 _queueEvents 投递回 UI 线程。</summary>
    private async Task PrefetchInstalledAsync(string udid)
    {
        if (_shuttingDown) return;          // 审查 P2-2
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _readback.Invalidate(udid);
            var apps = await _readback.ListInstalledAsync(udid, CancellationToken.None);
            sw.Stop();
            _queueEvents.Report(new InstalledAppsEvent(udid, apps));
            AppendLog($"[{udid[..Math.Min(8, udid.Length)]}] installed list read back: {apps.Count} apps, {sw.ElapsedMilliseconds} ms", LogKind.Detail);
        }
        catch (Exception ex)
        {
            AppendLog($"[{udid[..Math.Min(8, udid.Length)]}] failed to read back installed list: {ex.Message}", LogKind.Error);
        }
    }

    // ── 入队 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 故意保持同步：入队只做内存检查（BundleId / File.Exists / MinOs），不在这里 await 任何子进程；
    /// 设备身份、文件可读、空间、覆盖判断都由 QueueService 在安装前执行。不要再把 Probe 加回来。
    /// </summary>
    private Task EnqueueAsync()
    {
        var queue = SelectedDevice;
        var ipa = SelectedLibraryRow;
        if (queue is null || ipa is null || ipa.IsGroup) return Task.CompletedTask;
        TryEnqueueRowAsync(queue, ipa);
        return Task.CompletedTask;
    }

    /// <summary>右键单行「强制安装到当前设备」：只跳过最低 iOS 与空间检查，其他校验照旧。</summary>
    internal bool ForceInstallToCurrentDevice(LibraryRow? row)
    {
        var queue = SelectedDevice;
        if (queue is null || row is null || row.IsGroup) return false;
        return TryEnqueueRowAsync(queue, row, forceInstall: true);
    }

    /// <summary>
    /// 统一的"Cancel queued/running tasks"入口（2026-11-01 用户要求复用接口）：
    /// 资料库双击、队列双击、工具栏按钮都调这里；具体行为由 <see cref="QueueService.Cancel"/> 决定（07 §9 矩阵）。
    /// </summary>
    internal void CancelQueuedItem(DeviceQueueViewModel queue, Guid jobId, string displayName)
    {
        var outcome = _queue.Cancel(queue.Device.Udid, jobId);
        switch (outcome)
        {
            case QueueService.CancelOutcome.CancelledPending:
                AppendLog($"Canceled (not started): {displayName}", LogKind.End, queue.Device);
                ReportStatus($"[{queue.Device.ShortName}] canceled: {displayName}");
                break;

            case QueueService.CancelOutcome.CancelledRunning:
                AppendLog($"Canceled (upload interrupted): {displayName}", LogKind.End, queue.Device);
                ReportStatus($"[{queue.Device.ShortName}] canceled (upload interrupted): {displayName}");
                break;

            case QueueService.CancelOutcome.ScheduledAfterInstall:
                AppendLog($"Cannot cancel: {displayName} has entered the install phase and will be uninstalled automatically afterwards", LogKind.End, queue.Device);
                ReportStatus($"[{queue.Device.ShortName}] auto-uninstall after install: {displayName}");
                break;

            default:
                // AlreadyCancelled / NotCancellable / NotFound：无操作（不弹提示）
                break;
        }
    }

    /// <summary>队列行双击 = 取消该任务（复用 <see cref="CancelQueuedItem"/>，不重写逻辑）。</summary>
    internal void CancelJob(DeviceQueueViewModel queue, JobViewModel job)
        => CancelQueuedItem(queue, job.Id, job.FileName);

    /// <summary>
    /// 队列行双击（2026-11-02 用户定）：**任何时候都"Cancel/Withdraw"**——
    /// ① 同 BundleId 还有活任务（排队/上传/安装/卸载/暂停…）→ 取消它（复用统一取消入口）；
    /// ② 该行已安装成功（Done）→ **入队卸载**（而不是无反应）；
    /// ③ Failed / Cancelled → 无动作（已经结束，无可撤销）。
    /// </summary>
    internal void HandleQueueItemDoubleClick(DeviceQueueViewModel queue, JobViewModel job)
    {
        var udid = queue.Device.Udid;
        var live = _queue.Snapshot(udid).FirstOrDefault(x =>
            !x.Status.IsFinished() &&
            (x.Id == job.Id || string.Equals(x.BundleId, job.BundleId, StringComparison.OrdinalIgnoreCase)));

        if (live is not null)
        {
            CancelQueuedItem(queue, live.Id, job.FileName);
            return;
        }

        if (job.Status != QueueStatus.Done) return;

        EnqueueUninstall(queue, job.BundleId, job.DisplayNameText);
    }

    /// <summary>
    /// 卸载入队（队列双击 / 设备应用双击共用）：不做 Probe，检查交给 QueueService。
    /// </summary>
    internal bool EnqueueUninstall(DeviceQueueViewModel queue, string bundleId, string displayName)
    {
        var enqueued = _queue.Enqueue(queue.Device.Udid, JobKind.Uninstall, bundleId,
            filePath: null, packageKey: null, packageVersion: null,
            packageDisplayName: string.IsNullOrWhiteSpace(displayName) ? null : displayName);
        if (enqueued.Disposition == EnqueueDisposition.AlreadyRunning)
        {
            ReportStatus($"[{queue.Device.ShortName}] uninstall already queued: {bundleId}");
            return false;
        }

        if (enqueued.Disposition == EnqueueDisposition.KindConflict)
        {
            AppendLog($"Uninstall rejected: {bundleId} still has an install task in the queue ({enqueued.CurrentStatus.ToText()})", LogKind.Error, queue.Device);
            ReportError($"Uninstall rejected: [{queue.Device.ShortName}] still has an install task for this package ({enqueued.CurrentStatus.ToText()})");
            return false;
        }

        queue.AddJob(enqueued.Item);
        queue.ReorderJobs();
        _queue.StartPump(queue.Device.Udid);
        if (_isDeviceAppsMode) RefreshDeviceAppsMarkers();
        AppendLog($"Added to uninstall queue: {displayName} ({bundleId})", LogKind.End, queue.Device);
        ReportStatus($"[{queue.Device.ShortName}] added to uninstall queue: {displayName}");
        return true;
    }

    /// <summary>设备应用行双击：卸载该 BundleId（与队列双击同一个入队接口）。</summary>
    internal void UninstallDeviceApp(DeviceAppRow row)
    {
        var queue = SelectedDevice;
        if (queue is null) return;
        EnqueueUninstall(queue, row.BundleId, row.Name);
    }

    /// <summary>切换设备应用页多选；只在设备应用视图使用。</summary>
    public void ToggleDeviceAppsMultiSelectMode()
    {
        IsDeviceAppsMultiSelectMode = !IsDeviceAppsMultiSelectMode;
        if (!IsDeviceAppsMultiSelectMode) ClearDeviceAppsSelection();
        NotifyDeviceAppsSelectionChanged();
    }

    /// <summary>设备应用单击：多选模式下切换该行选中，返回 true 表示已处理。</summary>
    public bool ToggleDeviceAppMultiSelect(DeviceAppRow row)
    {
        if (!IsDeviceAppsMultiSelectMode) return false;
        row.IsMultiSelected = !row.IsMultiSelected;
        NotifyDeviceAppsSelectionChanged();
        return true;
    }

    public void SelectAllDeviceApps()
    {
        if (!IsDeviceAppsMultiSelectMode) return;
        foreach (var row in DeviceApps) row.IsMultiSelected = true;
        NotifyDeviceAppsSelectionChanged();
    }

    public void ClearDeviceAppsSelection()
    {
        foreach (var row in DeviceApps)
            if (row.IsMultiSelected) row.IsMultiSelected = false;
        NotifyDeviceAppsSelectionChanged();
    }

    /// <summary>退出设备应用多选并清空选择（切走视图 / 换设备时调用）。</summary>
    public void ExitDeviceAppsMultiSelect()
    {
        IsDeviceAppsMultiSelectMode = false;
        ClearDeviceAppsSelection();
    }

    /// <summary>批量卸载：所有选中行复用同一个卸载入队接口，不复制 QueueService 逻辑。</summary>
    public void UninstallSelectedDeviceApps()
    {
        var queue = SelectedDevice;
        if (queue is null) return;
        var selected = DeviceApps
            .Where(r => r.IsMultiSelected)
            .GroupBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (selected.Count == 0) return;

        var accepted = selected.Count(row => EnqueueUninstall(queue, row.BundleId, row.Name));
        ClearDeviceAppsSelection();
        IsDeviceAppsMultiSelectMode = false;
        var rejected = selected.Count - accepted;
        AppendLog($"Batch uninstall: {accepted} Apps submitted" +
                  (rejected > 0 ? $", rejected {rejected}" : ""), LogKind.End, queue.Device);
        ReportStatus($"[{queue.Device.ShortName}] Batch uninstall: {accepted} Apps submitted" +
                     (rejected > 0 ? $", rejected {rejected}" : ""));
    }

    /// <summary>刷新当前设备“设备应用”列表（只取已回读的用户 App，不含系统 App）。</summary>
    /// <summary>复用资料库的队列颜色口径：任一活跃任务→黄；否则任一失败→红；否则无。</summary>
    private void RefreshDeviceAppsMarkers()
    {
        var queue = SelectedDevice;
        if (queue is null) return;
        var snapshot = _queue.Snapshot(queue.Device.Udid);
        foreach (var row in DeviceApps)
        {
            OperationVisual visual = OperationVisual.None;
            foreach (var item in snapshot)
            {
                if (!string.Equals(item.BundleId, row.BundleId, StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = OperationVisuals.Resolve(item.Kind, item.Status);
                if (OperationVisuals.Priority(candidate) > OperationVisuals.Priority(visual))
                    visual = candidate;
            }

            row.Visual = visual;
            row.Marker = visual switch
            {
                OperationVisual.Failed => LibraryMarker.Failed,
                OperationVisual.Succeeded => LibraryMarker.Installed,
                OperationVisual.None => LibraryMarker.None,
                _ => LibraryMarker.Queued,
            };
        }
    }

    private async Task RefreshDeviceAppsAsync()
    {
        // 先让切换动作回到消息循环：设备应用页先显示出来，再构建行与取图标，
        // 避免设备 App 较多时切换按钮看起来“卡住”。
        await Task.Yield();

        var queue = SelectedDevice;
        if (queue is null)
        {
            DeviceApps.Clear();
            _deviceAppsSignature = "";
            DeviceAppsCountText = "Device apps: no device";
            ExitDeviceAppsMultiSelect();
            return;
        }

        var installed = queue.InstalledApps;
        if (installed is null)
        {
            DeviceApps.Clear();
            _deviceAppsSignature = "";
            DeviceAppsCountText = "Device apps: reading…";
            ExitDeviceAppsMultiSelect();
            if (_deviceAppsPrefetching.TryAdd(queue.Device.Udid, 0))
            {
                _ = SafeFireAndForget.Run(async () =>
                {
                    try { await PrefetchInstalledAsync(queue.Device.Udid); }
                    finally { _deviceAppsPrefetching.TryRemove(queue.Device.Udid, out _); }
                }, "Device application readback");
            }
            return;
        }

        // 同一设备、同一已安装集合：直接复用现有行与已解码图标，切走再切回不再重建/重取。
        var signature = queue.Device.Udid + "\u001f" + string.Join("\u001e",
            installed.Select(a => a.BundleId + "\u001d" + a.VersionKnown + "\u001d" + a.Version));
        if (string.Equals(signature, _deviceAppsSignature, StringComparison.Ordinal) && DeviceApps.Count > 0)
            return;

        _deviceAppsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _deviceAppsCts = cts;

        // 行构建放到后台；UI 线程只做一次集合替换。旧行在构建期间保留，避免切页时闪空表。
        var rows = await Task.Run(() => installed
            .Select(a => new DeviceAppRow
            {
                BundleId = a.BundleId,
                Name = string.IsNullOrWhiteSpace(a.DisplayName) ? a.BundleId : a.DisplayName,
                VersionText = a.VersionKnown ? a.Version : "Version unknown",
            })
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(), cts.Token).ConfigureAwait(true);
        if (cts.IsCancellationRequested || !ReferenceEquals(SelectedDevice, queue)) return;

        DeviceApps.Clear();
        foreach (var row in rows) DeviceApps.Add(row);
        _deviceAppsSignature = signature;
        ClearDeviceAppsSelection();
        DeviceAppsCountText = $"Device apps: {rows.Count}";
        RefreshDeviceAppsMarkers();

        var paths = await DeviceIconService.EnsureAsync(queue.Device.Udid, rows.Select(r => r.BundleId).ToList(), cts.Token)
            .ConfigureAwait(true);
        if (cts.IsCancellationRequested || !ReferenceEquals(SelectedDevice, queue)) return;

        var iconMap = await Task.Run(() => paths.ToDictionary(
            kv => kv.Key,
            kv => DeviceIconService.Load(kv.Value),
            StringComparer.OrdinalIgnoreCase), cts.Token).ConfigureAwait(true);
        if (cts.IsCancellationRequested || !ReferenceEquals(SelectedDevice, queue)) return;
        foreach (var row in rows)
        {
            if (iconMap.TryGetValue(row.BundleId, out var icon)) row.Icon = icon;
        }
    }

    /// <summary>
    /// 资料库行双击（2026-11-01 用户定）：不在当前设备队列 → 入队；已在队列 → 取消（按 07 §9 矩阵）。
    /// 双击判定在 code-behind（400 ms），这里只做动作。
    /// </summary>
    internal async Task ToggleLibraryQueueAsync(LibraryRow row)
    {
        var queue = SelectedDevice;
        if (queue is null || row.IsGroup || string.IsNullOrWhiteSpace(row.BundleId)) return;

        var installs = _queue.Snapshot(queue.Device.Udid)
            .Where(x => x.Kind == JobKind.Install)
            .ToList();

        var existing = installs.FirstOrDefault(x =>
            string.Equals(x.PackageKey, row.FilePath, StringComparison.OrdinalIgnoreCase));

        // 审查 N13：包被移动/改名后 PackageKey 还是旧路径 → 按 BundleId 回退（只在该 BundleId 唯一时）
        existing ??= installs.Count(x =>
            string.Equals(x.BundleId, row.BundleId, StringComparison.OrdinalIgnoreCase)) == 1
            ? installs.First(x => string.Equals(x.BundleId, row.BundleId, StringComparison.OrdinalIgnoreCase))
            : null;

        // 只有"still alive"的条目才算在队列里可取消；已取消/已完成/失败 → 当作未入队，重新入队（2026-11-01 修复）
        var stillLive = existing is not null && !existing.Status.IsFinished();

        if (stillLive)
        {
            CancelQueuedItem(queue, existing!.Id, row.NameText);
            return;
        }

        // 双击入队复用「安装 →」按钮的同一套逻辑（2026-11-01 用户要求）：
        // 先把它设为选中行，再走 EnqueueAsync（含 BundleId/文件存在/设备身份/版本闸门/入队提示）。
        SelectedLibraryRow = row;
        await EnqueueAsync();
    }

    /// <summary>把一行 IPA 加入指定设备队列（单包入队与双击入队共用；入队检查见 docs/07 §12）。</summary>
    private bool TryEnqueueRowAsync(DeviceQueueViewModel queue, LibraryRow ipa, bool fileExistsChecked = false, bool forceInstall = false)
    {
        // 入队前提：包已解析出 BundleId（07 §2）；解析失败不允许入队
        if (string.IsNullOrWhiteSpace(ipa.BundleId))
        {
            AppendLog($"Install request rejected: {ipa.NameText} has no BundleID, please retry", LogKind.Error);
            return false;
        }

        if (!fileExistsChecked && !File.Exists(ipa.FilePath))
        {
            // 入队时只做一次 File.Exists（UI 线程，快）；"exists + readable"的严格检查在
            // QueueService 执行前做（CheckPackageFile），这里不再重复（审核 P2-5）。
            ReportError("Could not enqueue: source file no longer exists (deleted or moved):" + ipa.FilePath);
            return false;
        }

        // 2026-11-05 用户定：瞬间入队——设备在线/身份、文件可读、空间检查全部延后到
        // QueueService 的泵内（安装前复用 Probe / ReadbackService）；这里只做内存检查。
        // 版本前置校验仍保留（纯内存，用的是刷新时已知的设备版本）。
        if (!forceInstall &&
            VersionRules.CheckMinimumOs(ipa.MinOs, queue.Device.Version) is { } minOsError)
        {
            AppendLog($"Enqueue rejected: {ipa.NameText} {minOsError}" +
                      "(Not uploaded; you can right-click the row to force installation)", LogKind.Error, queue.Device);
            ReportStatus($"Rejected: {ipa.NameText} {minOsError}");
            return false;
        }
        if (forceInstall)
        {
            AppendLog($"Force installation: {ipa.NameText} (skips minimum iOS and space checks)", LogKind.End, queue.Device);
            ReportStatus($"[{queue.Device.ShortName}] Forced installation: {ipa.NameText}");
        }

        // 覆盖安装：2026-11-01 用户决定不再提示、不弹确认（“老 iOS 没什么重要数据”）。

        var enqueued = _queue.Enqueue(
            queue.Device.Udid,
            JobKind.Install,
            ipa.BundleId,
            ipa.FilePath,
            packageKey: ipa.FilePath,
            packageVersion: ipa.PackageVersionForQueue,   // 版本未知给 null（审核 P2-2）
            expectedUncompressedBytes: ipa.AppSize > 0 ? ipa.AppSize : null,
            packageMinOs: string.IsNullOrWhiteSpace(ipa.MinOs) ? null : ipa.MinOs,
            packageDisplayName: string.IsNullOrWhiteSpace(ipa.Name) ? null : ipa.Name,
            packageOriginText: string.IsNullOrWhiteSpace(ipa.OriginText) ? null : ipa.OriginText,
            packageDeviceFamilyText: string.IsNullOrWhiteSpace(ipa.DeviceFamilyText) ? null : ipa.DeviceFamilyText,
            forceInstall: forceInstall);

        switch (enqueued.Disposition)
        {
            case EnqueueDisposition.KindConflict:
                AppendLog($"Install not queued: {ipa.NameText} still has an uninstall task in the queue ({enqueued.CurrentStatus.ToText()}); handle that first", LogKind.Error, queue.Device);
                ReportError($"{ipa.NameText} not queued: this package still has an uninstall task ({enqueued.CurrentStatus.ToText()})");
                return false;
            case EnqueueDisposition.AlreadyRunning:
                AppendLog($"{ipa.NameText} is already in the queue (current: {enqueued.CurrentStatus.ToText()}); click ignored", LogKind.End, queue.Device);
                ReportStatus($"{ipa.NameText} is already in the queue (current: {enqueued.CurrentStatus.ToText()}); this click was ignored");
                break;
            case EnqueueDisposition.Reset:
                AppendLog($"Re-queued at the end: {ipa.NameText}", LogKind.End, queue.Device);
                ReportStatus($"[{queue.Device.ShortName}] Re-queued at the end: {ipa.NameText}");
                break;
            default:
                AppendLog($"Enqueued (pending): {ipa.NameText}", LogKind.End, queue.Device);
                ReportStatus($"[{queue.Device.ShortName}] Added to install queue (pending): {ipa.NameText}");
                break;
        }

        queue.AddJob(enqueued.Item);
        queue.ReorderJobs();   // 2026-11-02：入队后立刻按优先级重排（大包靠上）
        _queue.StartPump(queue.Device.Udid);
        return true;
    }

    /// <summary>多选 / 单选统一的待入队文件集合（多选含被筛选隐藏的已选行）。</summary>
    private List<LibraryRow> GetInstallRows()
    {
        if (IsMultiSelectMode)
        {
            return _libraryView.AllFiles
                .Where(r => r.IsMultiSelected && !r.IsGroup && !string.IsNullOrWhiteSpace(r.BundleId))
                .ToList();
        }

        return SelectedLibraryRow is { IsGroup: false } row && !string.IsNullOrWhiteSpace(row.BundleId)
            ? new List<LibraryRow> { row }
            : new List<LibraryRow>();
    }

    public void EnqueueSelectionToDevice(DeviceQueueViewModel queue)
    {
        var rows = GetInstallRows();
        if (rows.Count == 0)
        {
            ReportStatus("No selected files to install");
            return;
        }

        var count = 0;
        foreach (var row in rows)
            if (TryEnqueueRowAsync(queue, row)) count++;
        if (count > 0)
            ReportStatus($"[{queue.Device.ShortName}] Enqueued {count} files");
    }

    public void EnqueueSelectionToAllOnline()
    {
        var targets = Devices.Where(d => d.Device.Online).ToList();
        if (targets.Count == 0)
        {
            ReportStatus("No online devices");
            return;
        }

        var rows = GetInstallRows();
        if (rows.Count == 0)
        {
            ReportStatus("No selected files to install");
            return;
        }

        // 批量：File.Exists 只查一次/行，避免 N×M 次磁盘 IO；泵内仍会再校验。
        var ready = rows.Where(r => File.Exists(r.FilePath)).ToList();
        if (ready.Count == 0)
        {
            ReportStatus("No files to enqueue (all source files are missing)");
            return;
        }

        var count = 0;
        foreach (var queue in targets)
            foreach (var row in ready)
                if (TryEnqueueRowAsync(queue, row, fileExistsChecked: true)) count++;
        ReportStatus($"Enqueued {ready.Count} files x {targets.Count} devices ({count} tasks total)");
    }

    public void EnqueueSelectionToUdids(IEnumerable<string> udids)
    {
        var set = new HashSet<string>(udids, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
        {
            ReportStatus("No devices selected");
            return;
        }

        var targets = Devices.Where(d => d.Device.Online && set.Contains(d.Device.Udid)).ToList();
        if (targets.Count == 0)
        {
            ReportStatus("No online devices");
            return;
        }

        var rows = GetInstallRows();
        if (rows.Count == 0)
        {
            ReportStatus("No selected files to install");
            return;
        }

        var ready = rows.Where(r => File.Exists(r.FilePath)).ToList();
        if (ready.Count == 0)
        {
            ReportStatus("No files to enqueue (all source files are missing)");
            return;
        }

        var count = 0;
        foreach (var queue in targets)
            foreach (var row in ready)
                if (TryEnqueueRowAsync(queue, row, fileExistsChecked: true)) count++;
        ReportStatus($"Enqueued {ready.Count} files x {targets.Count} devices ({count} tasks total)");
    }
}
