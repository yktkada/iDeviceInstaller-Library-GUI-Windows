using IdeviceGui.ViewModels;

namespace IdeviceGui.ViewModels.Library;

/// <summary>可搜索的列字段（2026-11-01 用户定：**全部列**都能搜，含被隐藏的列）。</summary>
internal enum SearchField
{
    Name,
    BundleId,
    FileName,
    Version,
    MinOs,
    DeviceFamily,
    Origin,
    Arch,
    FilePath,
    State,
    AppSize,
    FileSize,
}

/// <summary>匹配方式（2026-11-01 用户定：要给"精确匹配 / 前缀"这类开关）。</summary>
internal enum SearchMatchMode
{
    Contains,
    Prefix,
    Exact,
}

/// <summary>
/// 搜索范围（多选列字段）+ 匹配方式的偏好在 UI 上的投影。
/// 默认勾选：名称 / BundleId / 文件名（与旧行为一致）；默认匹配方式：包含。
/// 持久化键见 <see cref="LibraryPrefs.SearchScopeSettingKey"/> / <see cref="LibraryPrefs.SearchModeSettingKey"/>。
/// </summary>
internal sealed class SearchScope : ObservableObject
{
    public const string EmptyToken = "none";

    private bool _name = true;
    private bool _bundleId = true;
    private bool _fileName = true;
    private bool _version;
    private bool _minOs;
    private bool _deviceFamily;
    private bool _origin;
    private bool _arch;
    private bool _filePath;
    private bool _state;
    private bool _appSize;
    private bool _fileSize;

    public bool IsName { get => _name; set => SetProperty(ref _name, value); }
    public bool IsBundleId { get => _bundleId; set => SetProperty(ref _bundleId, value); }
    public bool IsFileName { get => _fileName; set => SetProperty(ref _fileName, value); }
    public bool IsVersion { get => _version; set => SetProperty(ref _version, value); }
    public bool IsMinOs { get => _minOs; set => SetProperty(ref _minOs, value); }
    public bool IsDeviceFamily { get => _deviceFamily; set => SetProperty(ref _deviceFamily, value); }
    public bool IsOrigin { get => _origin; set => SetProperty(ref _origin, value); }
    public bool IsArch { get => _arch; set => SetProperty(ref _arch, value); }
    public bool IsFilePath { get => _filePath; set => SetProperty(ref _filePath, value); }
    public bool IsState { get => _state; set => SetProperty(ref _state, value); }
    public bool IsAppSize { get => _appSize; set => SetProperty(ref _appSize, value); }
    public bool IsFileSize { get => _fileSize; set => SetProperty(ref _fileSize, value); }

    /// <summary>一个字段都没勾 = 不按字段过滤（返回全部）。</summary>
    public bool IsEmpty => !(_name || _bundleId || _fileName || _version || _minOs ||
                             _deviceFamily || _origin || _arch || _filePath || _state || _appSize || _fileSize);

    public IEnumerable<SearchField> Fields
    {
        get
        {
            if (_name) yield return SearchField.Name;
            if (_bundleId) yield return SearchField.BundleId;
            if (_fileName) yield return SearchField.FileName;
            if (_version) yield return SearchField.Version;
            if (_minOs) yield return SearchField.MinOs;
            if (_deviceFamily) yield return SearchField.DeviceFamily;
            if (_origin) yield return SearchField.Origin;
            if (_arch) yield return SearchField.Arch;
            if (_filePath) yield return SearchField.FilePath;
            if (_state) yield return SearchField.State;
            if (_appSize) yield return SearchField.AppSize;
            if (_fileSize) yield return SearchField.FileSize;
        }
    }

    public bool Includes(SearchField field) => field switch
    {
        SearchField.Name => _name,
        SearchField.BundleId => _bundleId,
        SearchField.FileName => _fileName,
        SearchField.Version => _version,
        SearchField.MinOs => _minOs,
        SearchField.DeviceFamily => _deviceFamily,
        SearchField.Origin => _origin,
        SearchField.Arch => _arch,
        SearchField.FilePath => _filePath,
        SearchField.State => _state,
        SearchField.AppSize => _appSize,
        SearchField.FileSize => _fileSize,
        _ => false,
    };

    public void Set(SearchField field, bool value)
    {
        switch (field)
        {
            case SearchField.Name: IsName = value; break;
            case SearchField.BundleId: IsBundleId = value; break;
            case SearchField.FileName: IsFileName = value; break;
            case SearchField.Version: IsVersion = value; break;
            case SearchField.MinOs: IsMinOs = value; break;
            case SearchField.DeviceFamily: IsDeviceFamily = value; break;
            case SearchField.Origin: IsOrigin = value; break;
            case SearchField.Arch: IsArch = value; break;
            case SearchField.FilePath: IsFilePath = value; break;
            case SearchField.State: IsState = value; break;
            case SearchField.AppSize: IsAppSize = value; break;
            case SearchField.FileSize: IsFileSize = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    /// <summary>整份复制（设置窗口的草稿/回滚用；只发一次汇总通知，不逐字段刷）。</summary>
    public void CopyFrom(SearchScope source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _name = source.IsName;
        _bundleId = source.IsBundleId;
        _fileName = source.IsFileName;
        _version = source.IsVersion;
        _minOs = source.IsMinOs;
        _deviceFamily = source.IsDeviceFamily;
        _origin = source.IsOrigin;
        _arch = source.IsArch;
        _filePath = source.IsFilePath;
        _state = source.IsState;
        _appSize = source.IsAppSize;
        _fileSize = source.IsFileSize;

        OnPropertyChanged(nameof(IsName));
        OnPropertyChanged(nameof(IsBundleId));
        OnPropertyChanged(nameof(IsFileName));
        OnPropertyChanged(nameof(IsVersion));
        OnPropertyChanged(nameof(IsMinOs));
        OnPropertyChanged(nameof(IsDeviceFamily));
        OnPropertyChanged(nameof(IsOrigin));
        OnPropertyChanged(nameof(IsArch));
        OnPropertyChanged(nameof(IsFilePath));
        OnPropertyChanged(nameof(IsState));
        OnPropertyChanged(nameof(IsAppSize));
        OnPropertyChanged(nameof(IsFileSize));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Fields));
    }

    /// <summary>字段在设置里的键名（稳定、与枚举名解耦）。</summary>
    public static string Key(SearchField field) => field switch
    {
        SearchField.Name => "name",
        SearchField.BundleId => "bundleId",
        SearchField.FileName => "fileName",
        SearchField.Version => "version",
        SearchField.MinOs => "minOs",
        SearchField.DeviceFamily => "deviceFamily",
        SearchField.Origin => "origin",
        SearchField.Arch => "arch",
        SearchField.FilePath => "filePath",
        SearchField.State => "state",
        SearchField.AppSize => "appSize",
        SearchField.FileSize => "fileSize",
        _ => field.ToString().ToLowerInvariant(),
    };

    public static bool TryParseKey(string raw, out SearchField field)
    {
        field = default;
        foreach (var candidate in Enum.GetValues<SearchField>())
        {
            if (!string.Equals(Key(candidate), raw.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            field = candidate;
            return true;
        }

        return false;
    }

    public string Format() => IsEmpty ? EmptyToken : string.Join(",", Fields.Select(Key));

    /// <summary>解析持久化文本：未配置 → 默认三项；<c>none</c> → 空范围；坏值 → 默认三项。</summary>
    public static SearchScope Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new SearchScope();
        if (string.Equals(raw.Trim().Trim('"'), EmptyToken, StringComparison.OrdinalIgnoreCase))
            return new SearchScope
            {
                IsName = false, IsBundleId = false, IsFileName = false,
            };

        var parsed = new SearchScope
        {
            IsName = false, IsBundleId = false, IsFileName = false,
        };
        var recognized = false;
        foreach (var part in raw.Trim().Trim('"').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseKey(part, out var field)) continue;
            recognized = true;
            parsed.Set(field, true);
        }

        return recognized ? parsed : new SearchScope();
    }
}

/// <summary>单字段匹配：包含 / 前缀 / 精确（都不分大小写）。</summary>
internal static class SearchMatch
{
    public static bool IsMatch(string? text, string query, SearchMatchMode mode)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return false;
        return mode switch
        {
            SearchMatchMode.Exact => string.Equals(text, query, StringComparison.OrdinalIgnoreCase),
            SearchMatchMode.Prefix => text.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            _ => text.Contains(query, StringComparison.OrdinalIgnoreCase),
        };
    }
}
