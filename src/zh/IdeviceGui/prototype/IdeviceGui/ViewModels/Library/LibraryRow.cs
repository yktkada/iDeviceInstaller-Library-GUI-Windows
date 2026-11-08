using IdeviceGui.Services.Data;
using IdeviceGui.Services.Parsing;
using IdeviceGui.ViewModels;

namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// 兼容判定的预留接口（M5 才会真正实现）。M4-b 先把状态列的红底渲染做出来，
/// 判定函数一律返回 <see cref="CompatState.Unknown"/> → 界面不标红。
/// </summary>
internal enum CompatState
{
    /// <summary>未判定（没有目标设备 / 数据不足 / M5 之前）。界面不标红。</summary>
    Unknown,

    /// <summary>已判定可安装。</summary>
    Compatible,

    /// <summary>已判定不可安装（硬冲突）。界面整格红底。</summary>
    Conflict,
}

/// <summary>不可安装的具体依据（M5 填充；现在恒为 null）。</summary>
internal sealed record CompatConflict(string Reason);

/// <summary>
/// 资料库网格的一行（docs/06 §3.1 / §3.5）：`PackageRow` 的**投影**，界面只认它，不把库行塞进 UI。
/// <para>
/// 常驻、按 <see cref="Id"/>（Packages.Id）复用：刷新时**原地改属性**并只对变化的字段发通知，
/// 不重建集合（硬约束 #8）、不丢选中与滚动位置。
/// </para>
/// </summary>
internal sealed class LibraryRow : ObservableObject
{
    private long _appSize;
    private long _fileSize;
    private string _name = "";
    private string _version = "";
    private string _build = "";
    private string _minOs = "";
    private string _deviceFamily = "";
    private string _deviceFamilyText = "";
    private string? _launchImageJson;
    private string _lastRawFamily = "\u0000";

    /// <summary>设备族文案是否用 Retina（设置键 Grid.DeviceFamilyRetinaText，默认 true；关 = 用 R）。</summary>
    internal static bool UseRetinaText { get; set; } = true;
    private string _bundleId = "";
    private string _arch = "";
    private string _parseState = ParseStates.Pending;
    private string? _parseError;
    private string? _iconKey;
    private object? _icon;
    private CompatState _compat = CompatState.Unknown;
    private CompatConflict? _compatConflict;
    private bool _isCollapsed;
    private int _groupCount;
    private bool _isGroupMember;
    private bool _existsFlag = true;
    private bool _isGroupEnd;
    private bool _isMultiSelected;
    private bool _isAppendedSeparator;
    private ulong? _volumeSerial;
    private ulong? _fileIdHigh;
    private ulong? _fileIdLow;
    private string? _normalizedPath;

    /// <summary>库主键（`Packages.Id`）：行复用的唯一键；组行用负数、临时行用外部给的负数。</summary>
    public long Id { get; init; }

    /// <summary>组行（同 BundleId 多个包合并出来的那一行）。</summary>
    public bool IsGroup { get; init; }

    /// <summary>
    /// 合并组的**最后一行**（2026-11-02 用户定：组块的"下分割线"画在这里）：
    /// 收起时 = 组行本身；展开时 = 组内最后一个成员行。
    /// </summary>
    public bool IsGroupEnd
    {
        get => _isGroupEnd;
        set => SetProperty(ref _isGroupEnd, value);
    }

    /// <summary>多选模式下的选中状态（2026-11-07；仅本次运行，不落库）。</summary>
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set => SetProperty(ref _isMultiSelected, value);
    }

    /// <summary>「隐藏但已选」行上方的分隔线（2026-11-08 用户要求，不是真实文件行）。</summary>
    public bool IsAppendedSeparator { get => _isAppendedSeparator; set => SetProperty(ref _isAppendedSeparator, value); }

    /// <summary>文件身份（分组绑定用；2026-11-08）。</summary>
    public ulong? VolumeSerial { get => _volumeSerial; set => SetProperty(ref _volumeSerial, value); }
    public ulong? FileIdHigh { get => _fileIdHigh; set => SetProperty(ref _fileIdHigh, value); }
    public ulong? FileIdLow { get => _fileIdLow; set => SetProperty(ref _fileIdLow, value); }
    public string? NormalizedPath { get => _normalizedPath; set => SetProperty(ref _normalizedPath, value); }

    /// <summary>组行当前是否收起（收起后不显示成员文件行）。</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => SetProperty(ref _isCollapsed, value);
    }

    /// <summary>组内包数（「N 个」徽标）。</summary>
    public int GroupCount
    {
        get => _groupCount;
        set
        {
            if (!SetProperty(ref _groupCount, value)) return;
            OnPropertyChanged(nameof(GroupText));
            OnPropertyChanged(nameof(NameText));
        }
    }

    /// <summary>组行的 BundleId（展开/收起时作为 key）。</summary>
    public string GroupBundleId { get; init; } = "";

    /// <summary>「添加 IPA…」加进来、还没入库的行（`Packages` 里没有）。</summary>
    public bool IsTransient { get; init; }

    /// <summary>已安装标记（2026-11-01：最前 ✓ 列；2026-11-02：组行 = 任一成员已装 → 打勾，`◐` 废弃）。</summary>
    private LibraryInstalledState _installedState = LibraryInstalledState.None;
    public LibraryInstalledState InstalledState
    {
        get => _installedState;
        set
        {
            if (!SetProperty(ref _installedState, value)) return;
            OnPropertyChanged(nameof(IsInstalledTicked));
            OnPropertyChanged(nameof(IsInstalledOtherVersion));
            OnPropertyChanged(nameof(IsGroupInstalledTicked));
        }
    }

    /// <summary>
    /// 该行是否打勾（2026-11-02 用户定）：勾列整格绿底用它。**用 bool 而不是直接绑 enum**——
    /// CellStyle 里绑 `Marker` 的 DataTrigger 实测不触发（踩坑 C35），bool 派生属性稳定。
    /// </summary>
    public bool IsInstalledTicked => InstalledState == LibraryInstalledState.Yes;

    /// <summary>组行：任一成员已安装（同版本或其他版本）→ 勾列绿底 + ✓。</summary>
    public bool IsGroupInstalledTicked => IsGroup && InstalledState != LibraryInstalledState.None;

    /// <summary>同 BundleId 已安装但版本不同/未知 → 勾列浅蓝底（不显示勾）。</summary>
    public bool IsInstalledOtherVersion => InstalledState == LibraryInstalledState.OtherVersion;

    /// <summary>
    /// 行标记色（2026-11-02 用户定：黄 &gt; 红 &gt; 绿 &gt; 无）：
    /// 黄=在队列、红=失败、绿=**队列安装成功**（临时，只存内存，刷新即清）。
    /// 组行 = 成员聚合（见 <see cref="LibraryMarkers.ResolveGroup"/>），绿画在组行图标列。
    /// </summary>
    private LibraryMarker _marker = LibraryMarker.None;
    public LibraryMarker Marker
    {
        get => _marker;
        set
        {
            if (!SetProperty(ref _marker, value)) return;
            OnPropertyChanged(nameof(IsGroupQueued));
            OnPropertyChanged(nameof(IsGroupFailed));
            OnPropertyChanged(nameof(IsGroupInstalledMarked));
        }
    }

    /// <summary>组行「成员里有在队列的」→ 图标列染黄（2026-11-02 用户定）。</summary>
    public bool IsGroupQueued => IsGroup && Marker == LibraryMarker.Queued;

    /// <summary>组行「成员里有安装失败的」→ 图标列染红。</summary>
    public bool IsGroupFailed => IsGroup && Marker == LibraryMarker.Failed;

    /// <summary>组行「成员里有安装成功的」→ 图标列染绿。</summary>
    public bool IsGroupInstalledMarked => IsGroup && Marker == LibraryMarker.Installed;

    private OperationVisual _visual;
    private OperationVisual _groupVisual;

    /// <summary>文件行的统一操作颜色；组行不用它铺整行。</summary>
    public OperationVisual Visual
    {
        get => _visual;
        set
        {
            if (!SetProperty(ref _visual, value)) return;
            OnPropertyChanged(nameof(RowBrush));
        }
    }

    /// <summary>组行 icon 列状态；只按匹配当前设备已安装版本的成员聚合。</summary>
    public OperationVisual GroupVisual
    {
        get => _groupVisual;
        set
        {
            if (!SetProperty(ref _groupVisual, value)) return;
            OnPropertyChanged(nameof(GroupIconBrush));
        }
    }

    public System.Windows.Media.Brush RowBrush => _visual.ToBrush();
    public System.Windows.Media.Brush GroupIconBrush => _groupVisual.ToBrush();

    /// <summary>双击待确认（armed）：第一次按下后 400 ms 内为 true，行显示浅蓝底（2026-11-01）。</summary>
    private bool _isArmed;
    public bool IsArmed
    {
        get => _isArmed;
        set => SetProperty(ref _isArmed, value);
    }

    /// <summary>组内成员行（缩进显示，和组行区分）。每次 <c>Build()</c> 时按当前合并状态重设。</summary>
    public bool IsGroupMember
    {
        get => _isGroupMember;
        set { if (SetProperty(ref _isGroupMember, value)) OnPropertyChanged(nameof(NameText)); }
    }

    /// <summary>「N 个」文案（组行才有）。</summary>
    /// <summary>组内个数（2026-11-01：显示在图标列，不再拼进名称）。</summary>
    public string GroupText => IsGroup ? $"{GroupCount} 个" : "";

    /// <summary>名称列显示：组行带「N 个」、成员行缩进、临时行带标记。</summary>
    public string NameText => IsGroup
        ? Name
        : (IsGroupMember ? "    └ " : "") + Name + (IsTransient ? "  ·未入库" : "");

    // 路径必须**可更新**：扫描识别到"移动/改名"时同一个 Id 的路径会变（审核 P1-1；
    // 原先是 init-only，UpdateFrom 写不进去 → 入队仍用旧路径，报"源文件已不存在"）
    private string _filePath = "";
    private string _fileName = "";

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value ?? "")) OnPropertyChanged(nameof(FilePathText));
        }
    }

    public string FileName { get => _fileName; set => SetProperty(ref _fileName, value ?? ""); }

    /// <summary>所属扫描根（`Packages.ScanRootId`；0 = 不属于任何根，如「添加文件…」临时行）。</summary>
    public long ScanRootId { get => _scanRootId; set => SetProperty(ref _scanRootId, value); }

    private long _scanRootId;

    /// <summary>根的显示名（`Root1`…）；空 = 不属于任何根（路径列显示完整路径）。</summary>
    public string RootLabel
    {
        get => _rootLabel;
        set
        {
            if (SetProperty(ref _rootLabel, value ?? "")) OnPropertyChanged(nameof(FilePathText));
        }
    }

    private string _rootLabel = "";

    /// <summary>根的完整路径（去掉前缀用）。</summary>
    public string RootPath
    {
        get => _rootPath;
        set
        {
            if (SetProperty(ref _rootPath, value ?? "")) OnPropertyChanged(nameof(FilePathText));
        }
    }

    private string _rootPath = "";

    /// <summary>
    /// 路径列的**显示文案**（2026-11-02 用户定）：属于某个根 → `Rootx\子路径`；否则 → **完整路径**。
    /// 复制/打开资源管理器仍用 <see cref="FilePath"/>（显示与动作分离）；搜索与排序也按完整路径。
    /// </summary>
    public string FilePathText
    {
        get
        {
            if (string.IsNullOrEmpty(_filePath)) return "";
            if (string.IsNullOrEmpty(_rootLabel) || string.IsNullOrEmpty(_rootPath)) return _filePath;
            if (!_filePath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase)) return _filePath;

            var rest = _filePath[_rootPath.Length..].TrimStart('\\', '/');
            return rest.Length == 0 ? _rootLabel : _rootLabel + "\\" + rest;
        }
    }

    /// <summary>扫描后该文件是否还存在（false = 已删除/移走；2026-11-02 用户定：仍显示，置灰并排到最后）。</summary>
    public bool ExistsFlag
    {
        get => _existsFlag;
        set
        {
            if (!SetProperty(ref _existsFlag, value)) return;
            OnPropertyChanged(nameof(IsMissing));
            NotifyStateChanged();
        }
    }

    public bool IsMissing => !_existsFlag;

    public string? AppDirectory { get; set; }
    public string? Sdk { get; init; }
    public string? RequiredCaps { get; init; }

    /// <summary>解压后大小：`.app` 未压缩总量（docs/06 §2 的「体积」列）。</summary>
    public long AppSize { get => _appSize; set => SetProperty(ref _appSize, value); }

    /// <summary>原始文件大小：IPA 本身（「原始大小」列）。</summary>
    public long FileSize { get => _fileSize; set => SetProperty(ref _fileSize, value); }

    public string Name { get => _name; set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(NameText)); } }
    public string Version { get => _version; set => SetProperty(ref _version, value); }
    public string Build { get => _build; set => SetProperty(ref _build, value); }
    public string MinOs { get => _minOs; set => SetProperty(ref _minOs, value); }
    public string DeviceFamily { get => _deviceFamily; set => SetProperty(ref _deviceFamily, value); }

    /// <summary>
    /// 「设备族」列显示文案（M5-lite，2026-11-04）：由原始 family + `LaunchImageJson` 计算，见
    /// <see cref="DeviceAdaptation"/>。缺 `UIDeviceFamily` 时为 <c>unknown</c>。
    /// </summary>
    public string DeviceFamilyText { get => _deviceFamilyText; set => SetProperty(ref _deviceFamilyText, value); }

    private string _originKind = "";

    /// <summary>IPA 来源（PackageOrigin 常量；2026-11-05）。</summary>
    public string OriginKind
    {
        get => _originKind;
        set
        {
            if (SetProperty(ref _originKind, value)) OnPropertyChanged(nameof(OriginText));
        }
    }

    /// <summary>来源列显示：组行不显示；文件行三选一。</summary>
    public string OriginText => PackageOrigin.DisplayText(_originKind);
    public string BundleId { get => _bundleId; set => SetProperty(ref _bundleId, value); }
    public string Arch
    {
        get => _arch;
        set
        {
            if (SetProperty(ref _arch, value)) OnPropertyChanged(nameof(ArchText));
        }
    }

    /// <summary>
    /// 架构列的显示文案（2026-11-02 用户定）：32 位去掉 `arm` 前缀（`v6/v7/v7s/v7f`），`arm64` 保留；
    /// 固定顺序 **v6 → v7 → v7f → v7s → arm64 → 未知**，去重。
    /// </summary>
    public string ArchText => FormatArch(_arch);

    internal static string FormatArch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var tokens = raw
            .Split(new[] { ',', ';', '，', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select(t => t.Equals("arm64", StringComparison.OrdinalIgnoreCase)
                ? "arm64"
                : t.StartsWith("arm", StringComparison.OrdinalIgnoreCase) ? t[3..] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        static int Rank(string token) => token.ToLowerInvariant() switch
        {
            "v6" => 0,
            "v7" => 1,
            "v7f" => 2,
            "v7s" => 3,
            "arm64" => 4,
            _ => 5,
        };

        tokens.Sort((left, right) =>
        {
            var rank = Rank(left).CompareTo(Rank(right));
            return rank != 0 ? rank : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        });

        return string.Join(", ", tokens);
    }
    public string ParseState
    {
        get => _parseState;
        set { if (SetProperty(ref _parseState, value)) NotifyStateChanged(); }
    }

    public string? ParseError
    {
        get => _parseError;
        set { if (SetProperty(ref _parseError, value)) NotifyStateChanged(); }
    }

    /// <summary>图标缓存键（`data\icons\&lt;key&gt;.png`）；变化时才重新加载图片。</summary>
    public string? IconKey { get => _iconKey; set => SetProperty(ref _iconKey, value); }

    /// <summary>已解码并 <c>Freeze()</c> 的图标（界面直接绑，不再逐次解码）。</summary>
    public object? Icon { get => _icon; set => SetProperty(ref _icon, value); }

    /// <summary>兼容判定（M5 之前恒为 Unknown；状态列据此标红，**不加额外列**）。</summary>
    public CompatState Compat
    {
        get => _compat;
        set
        {
            if (!SetProperty(ref _compat, value)) return;
            NotifyStateChanged();
            OnPropertyChanged(nameof(IsCompatConflict));
        }
    }

    public CompatConflict? CompatConflict
    {
        get => _compatConflict;
        set
        {
            if (!SetProperty(ref _compatConflict, value)) return;
            NotifyStateChanged();
        }
    }

    /// <summary>状态列红底触发位（M5 接上判定后才会变 true）。</summary>
    public bool IsCompatConflict => Compat == CompatState.Conflict;

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateTooltip));
    }

    /// <summary>状态列文字：解析失败优先（没有数据就谈不上兼容）；兼容冲突时给短原因。</summary>
    public string StateText => IsMissing
        ? "文件缺失"
        : ParseState switch
    {
        ParseStates.Failed => "解析未完成" + (string.IsNullOrEmpty(ParseError) ? "" : "：" + Shorten(ParseError)),
        ParseStates.Pending => "未解析",
        _ => Compat == CompatState.Conflict
            ? "不可安装" + (CompatConflict is null ? "" : "：" + CompatConflict.Reason)
            : "正常",
    };

    /// <summary>状态列的详细依据（鼠标悬停 / 以后的信息区用）。</summary>
    public string StateTooltip => ParseState switch
    {
        ParseStates.Failed => ParseError ?? "解析未完成",
        ParseStates.Pending => "尚未解析该包，尝试「全量重扫」。",
        _ => Compat switch
        {
            CompatState.Conflict => "与当前设备不兼容：" + (CompatConflict?.Reason ?? "存在硬冲突"),
            CompatState.Compatible => "此包与当前设备兼容。",
            _ => "未能判定兼容性",
        },
    };

    /// <summary>
    /// 组行的「体积」列：**最小–最大**（2026-10-31 用户要求，不再相加）；单值时就显示单值。
    /// 非组行为 null，走 <see cref="AppSizeText"/> 的单行逻辑。
    /// </summary>
    public string? RangeAppSizeText { get; init; }

    /// <summary>组行的「最低 iOS」列：**最小–最大**（同上）。</summary>
    public string? RangeMinOsText { get; init; }

    /// <summary>组行的「原始大小」列：**最小–最大**（2026-11-07 用户要求；不再显示合计）。</summary>
    public string? RangeFileSizeText { get; init; }

    /// <summary>体积列的显示：组行给范围；单行给解压后大小（0 = 未知 → 占位，不显示 0 B）。</summary>
    public string AppSizeText => RangeAppSizeText ?? (AppSize > 0 ? FormatBytes(AppSize) : "—");

    /// <summary>最低 iOS 列的显示：组行给范围，单行原样。</summary>
    public string MinOsText => RangeMinOsText ?? (string.IsNullOrEmpty(MinOs) ? "—" : MinOs);

    /// <summary>原始大小列：组行显示最小–最大；单行显示原值（0 = 未知 → 占位）。</summary>
    public string FileSizeText => RangeFileSizeText ?? (FileSize > 0 ? FormatBytes(FileSize) : "—");

    /// <summary>版本列：ShortVersion 缺则回退 Build；都缺显示占位 `—`（只是显示，不参与判定）。</summary>
    public string VersionText => string.IsNullOrEmpty(Version)
        ? (string.IsNullOrEmpty(Build) ? "—" : Build)
        : Version;

    /// <summary>
    /// 入队 / 恢复判定用的版本：**未知时给 <see langword="null"/>**（审核 P2-2）。
    /// 若把显示占位 `—` 当成版本传下去，<c>VersionsMatch</c> 会判"不一致"→ 中断恢复走重装 →
    /// 覆盖安装清空 App 数据（AGENTS §5.7、docs/07 §3.5 要求"版本无法取得只按 BundleId 判定"）。
    /// </summary>
    public string? PackageVersionForQueue =>
        string.IsNullOrWhiteSpace(Version) && string.IsNullOrWhiteSpace(Build) ? null : VersionText;

    /// <summary>图标任务回填时的键校验：只有调度时那版图标键仍然有效才赋值（审核 P2-3）。</summary>
    public bool HasIconKey(string? key) =>
        string.Equals(IconKey ?? "", key ?? "", StringComparison.Ordinal);

    /// <summary>设置切换 Retina/R 文案后重算本行显示串（不重读库）。</summary>
    internal void RefreshDeviceFamilyText()
        => DeviceFamilyText = DeviceAdaptation.Describe(_lastRawFamily, _launchImageJson, UseRetinaText);

    /// <summary>「隐藏但已选」分界行。</summary>
    internal static LibraryRow CreateAppendedSeparator() => new()
    {
        Id = long.MinValue,
        IsAppendedSeparator = true,
    };

    /// <summary>用一行库数据原地更新（只在值变化时发通知）。</summary>
    public void UpdateFrom(PackageRow row)
    {
        // 路径先更新：扫描识别"移动 / 改名"时同一 Id 的 FilePath 会变（审核 P1-1）
        FilePath = row.FilePath ?? "";
        FileName = row.FileName ?? "";
        NormalizedPath = row.NormalizedPath;
        VolumeSerial = row.VolumeSerial;
        FileIdHigh = row.FileIdHigh;
        FileIdLow = row.FileIdLow;
        ScanRootId = row.ScanRootId;
        AppDirectory = row.AppDirName;
        AppSize = row.AppSize;
        FileSize = row.FileSize;
        Name = string.IsNullOrEmpty(row.DisplayName) ? (row.FileName ?? "") : row.DisplayName;
        Version = row.Version ?? "";
        Build = row.Build ?? "";
        MinOs = row.MinOs ?? "";
        DeviceFamily = row.DeviceFamily ?? "";
        var launchJson = row.LaunchImageJson ?? "";
        if (!string.Equals(_lastRawFamily, DeviceFamily, StringComparison.Ordinal) ||
            !string.Equals(_launchImageJson, launchJson, StringComparison.Ordinal))
        {
            _lastRawFamily = DeviceFamily;
            _launchImageJson = launchJson;
            DeviceFamilyText = DeviceAdaptation.Describe(DeviceFamily, launchJson, UseRetinaText);
        }
        OriginKind = row.OriginKind ?? "";
        BundleId = row.BundleId ?? "";
        Arch = row.Arch ?? "";
        ParseState = row.ParseState;
        ParseError = row.ParseError;
        ExistsFlag = row.ExistsFlag;

        var iconKey = row.IconKey;
        if (!string.Equals(IconKey, iconKey, StringComparison.Ordinal))
        {
            IconKey = iconKey;
            Icon = null;              // 键变了：等后台重新加载
        }
    }

    /// <summary>给列表 / UIA 用的一行文字（`DisplayMemberPath` 不影响 UIA 名称，见 AGENTS §6 第 3 条）。</summary>
    public override string ToString() => $"{NameText} {VersionText} ({BundleId}) {Arch}";

    /// <summary>
    /// 体积范围（组行用）：**同单位只写一次单位**，如 `0.91–1.92 GB`；不同单位才各带一次（`95.4 MB–1.92 GB`）。
    /// 两端相同只给单值。这样在 92–132 DIP 的列宽里不会被裁掉开头（2026-10-31 用户反馈的"显示 9.1GB−1.92GB"就是裁字造成的）。
    /// </summary>
    internal static string FormatBytesRange(long min, long max)
    {
        var low = FormatBytes(min);
        var high = FormatBytes(max);
        if (string.Equals(low, high, StringComparison.Ordinal)) return low;

        var lowParts = FormatBytesParts(min);
        var highParts = FormatBytesParts(max);
        if (string.Equals(lowParts.Unit, highParts.Unit, StringComparison.Ordinal))
        {
            return $"{lowParts.Value}–{highParts.Value} {lowParts.Unit}";
        }

        return low + "–" + high;
    }

    /// <summary>体积的"数值 + 单位"两部分（复审 C1：范围拼接不再靠拆字符串，避免改格式时静默错位）。</summary>
    internal static (string Value, string Unit) FormatBytesParts(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => ((bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00"), "GB"),
        >= 1024 * 1024 => ((bytes / 1024.0 / 1024.0).ToString("0.0"), "MB"),
        >= 1024 => ((bytes / 1024.0).ToString("0.0"), "KB"),
        _ => (bytes.ToString(), "B"),
    };

    internal static string FormatBytes(long bytes)
    {
        var (value, unit) = FormatBytesParts(bytes);
        return value + " " + unit;
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";


}
