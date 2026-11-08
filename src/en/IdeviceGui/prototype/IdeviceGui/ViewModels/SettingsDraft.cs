using IdeviceGui.Services;
using IdeviceGui.ViewModels.Library;

namespace IdeviceGui.ViewModels;

/// <summary>
/// 设置窗口的**草稿**（2026-11-02 用户定：窗口内即时预览、**点确定/应用才写库**，取消/关窗回滚）。
/// <para>
/// - 只放"可预览"的设置；列宽/列顺序、搜索框内的即时操作不在窗口里（docs/01 §2.4）。<br/>
/// - 默认值与各键默认口径见 docs/09 §3.2。
/// </para>
/// </summary>
internal sealed class SettingsDraft
{
    // ── 资料库 ──
    public double RowHeight { get; set; } = 32;
    public LibrarySort Sort { get; set; } = new(LibrarySortField.AppSize, Descending: true);
    public SearchScope Scope { get; set; } = new();
    public SearchMatchMode MatchMode { get; set; } = SearchMatchMode.Contains;
    public int SearchDebounceRows { get; set; } = LibraryPrefs.DefaultSearchDebounceRows;
    public int SearchDebounceMs { get; set; } = LibraryPrefs.DefaultSearchDebounceMs;
    public bool DeviceFamilyRetinaText { get; set; } = true;
    public bool MergeSameBundle { get; set; } = true;
    public bool ShowMissingFiles { get; set; } = true;

    // ── 扫描 ──
    public int ScanBatchSize { get; set; } = 30;

    // ── 图标 ──
    public int IconCacheMaxMb { get; set; } = 2048;
    public bool IconMatchDisplayScale { get; set; } = true;
    public bool IconDownscaleCache { get; set; } = true;
    public int IconBucketRound { get; set; } = 16;

    // ── 日志 ──
    public bool LogToFile { get; set; }
    public bool LogDetailMode { get; set; }

    // ── 布局（三栏比例由窗口上的按钮即时复位，不进草稿）──
    public bool LogHidden { get; set; }
    public double LogOverlayWidth { get; set; } = 420;

    // ── 设备 ──
    public int PollSeconds { get; set; } = 2;
    public bool AutoResume { get; set; } = true;
    public int ReconnectStableSeconds { get; set; } = 3;
    public int AutoRetryAttempts { get; set; } = 2;
    public IReadOnlyList<DeviceInfoFieldSetting> DeviceFields { get; set; } = DeviceInfoFields.Default;

    /// <summary>深拷贝（"取消/回滚"用）。</summary>
    public SettingsDraft Clone() => new()
    {
        RowHeight = RowHeight,
        Sort = new LibrarySort(Sort.Field, Sort.Descending),
        Scope = CloneScope(Scope),
        MatchMode = MatchMode,
        SearchDebounceRows = SearchDebounceRows,
        SearchDebounceMs = SearchDebounceMs,
        DeviceFamilyRetinaText = DeviceFamilyRetinaText,
        MergeSameBundle = MergeSameBundle,
        ShowMissingFiles = ShowMissingFiles,
        ScanBatchSize = ScanBatchSize,
        IconCacheMaxMb = IconCacheMaxMb,
        IconMatchDisplayScale = IconMatchDisplayScale,
        IconDownscaleCache = IconDownscaleCache,
        IconBucketRound = IconBucketRound,
        LogToFile = LogToFile,
        LogDetailMode = LogDetailMode,
        LogHidden = LogHidden,
        LogOverlayWidth = LogOverlayWidth,
        PollSeconds = PollSeconds,
        AutoResume = AutoResume,
        ReconnectStableSeconds = ReconnectStableSeconds,
        AutoRetryAttempts = AutoRetryAttempts,
        DeviceFields = DeviceFields.ToList(),
    };

    internal static SearchScope CloneScope(SearchScope source)
    {
        var clone = new SearchScope();
        clone.CopyFrom(source);
        return clone;
    }

    /// <summary>某分组的"恢复默认"（只动该组字段）。</summary>
    public void ResetGroup(SettingsGroup group)
    {
        switch (group)
        {
            case SettingsGroup.Library:
                RowHeight = 32;
                Sort = new LibrarySort(LibrarySortField.AppSize, Descending: true);
                Scope = new SearchScope();
                MatchMode = SearchMatchMode.Contains;
                SearchDebounceRows = LibraryPrefs.DefaultSearchDebounceRows;
                SearchDebounceMs = LibraryPrefs.DefaultSearchDebounceMs;
                DeviceFamilyRetinaText = true;
                MergeSameBundle = true;
                ShowMissingFiles = true;
                break;
            case SettingsGroup.Scan:
                ScanBatchSize = 30;
                break;
            case SettingsGroup.Icons:
                IconCacheMaxMb = 2048;
                IconMatchDisplayScale = true;
                IconDownscaleCache = true;
                IconBucketRound = 16;
                break;
            case SettingsGroup.Log:
                LogToFile = false;
                LogDetailMode = false;
                break;
            case SettingsGroup.Layout:
                LogHidden = false;
                LogOverlayWidth = 420;
                break;
            case SettingsGroup.Device:
                PollSeconds = 2;
                AutoResume = true;
                ReconnectStableSeconds = 3;
                AutoRetryAttempts = 2;
                DeviceFields = DeviceInfoFields.Default;
                break;
        }
    }
}

/// <summary>设置窗口的分组（每组一个「恢复默认」，docs/01 §2.4）。</summary>
internal enum SettingsGroup
{
    Library,
    Scan,
    Icons,
    Log,
    Layout,
    Device,
}
