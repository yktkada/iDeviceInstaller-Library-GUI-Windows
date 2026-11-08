using System;
using System.Globalization;

namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// 资料库视图偏好（2026-11-01）：排序持久化文本与行高夹紧。纯函数，便于单测。
/// </summary>
internal static class LibraryPrefs
{
    public const string SortSettingKey = "Grid.Sort";
    public const string RowHeightSettingKey = "Grid.RowHeight";

    /// <summary>搜索范围（多选列字段，2026-11-01 用户定）。</summary>
    public const string SearchScopeSettingKey = "Grid.SearchScope";

    /// <summary>匹配方式：包含 / 前缀 / 精确（2026-11-01 用户定）。</summary>
    public const string SearchModeSettingKey = "Grid.SearchMode";

    /// <summary>搜索防抖的行数阈值：显示行数**大于**该值才延迟（0–200000，默认 1000）。</summary>
    public const string SearchDebounceRowsSettingKey = "Grid.SearchDebounceRows";

    /// <summary>搜索防抖延迟毫秒（0–5000，默认 200；0 = 下一轮消息循环立即生效）。</summary>
    public const string SearchDebounceMsSettingKey = "Grid.SearchDebounceMs";

    /// <summary>设备族 Retina 文案开关（true=Retina，false=R；2026-11-05 用户定，默认 true）。</summary>
    public const string DeviceFamilyRetinaSettingKey = "Grid.DeviceFamilyRetinaText";

    public const int DefaultSearchDebounceRows = 1000;
    public const int MinSearchDebounceRows = 0;
    public const int MaxSearchDebounceRows = 200000;

    public const int DefaultSearchDebounceMs = 200;
    public const int MinSearchDebounceMs = 0;
    public const int MaxSearchDebounceMs = 5000;

    public const double MinRowHeight = 16;
    public const double MaxRowHeight = 48;
    public const double DefaultRowHeight = 32;

    /// <summary>序列化成 <c>Field:desc</c> / <c>Field:asc</c>。</summary>
    public static string FormatSort(LibrarySort sort)
        => sort.Field + (sort.Descending ? ":desc" : ":asc");

    /// <summary>解析排序偏好；坏值/未知字段返回 <paramref name="fallback"/>。</summary>
    public static LibrarySort ParseSort(string? raw, LibrarySort fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        var parts = raw.Trim().Trim('"').Split(':');
        if (parts.Length != 2) return fallback;
        if (!Enum.TryParse<LibrarySortField>(parts[0], ignoreCase: true, out var field)) return fallback;

        return parts[1].Trim().ToLowerInvariant() switch
        {
            "desc" or "descending" => new LibrarySort(field, Descending: true),
            "asc" or "ascending" => new LibrarySort(field, Descending: false),
            _ => fallback,
        };
    }

    /// <summary>匹配方式序列化（稳定小写键）。</summary>
    public static string FormatSearchMode(SearchMatchMode mode) => mode switch
    {
        SearchMatchMode.Prefix => "prefix",
        SearchMatchMode.Exact => "exact",
        _ => "contains",
    };

    /// <summary>解析匹配方式；坏值回 <paramref name="fallback"/>。</summary>
    public static SearchMatchMode ParseSearchMode(string? raw, SearchMatchMode fallback = SearchMatchMode.Contains)
        => raw?.Trim().Trim('"').ToLowerInvariant() switch
        {
            "prefix" => SearchMatchMode.Prefix,
            "exact" => SearchMatchMode.Exact,
            "contains" => SearchMatchMode.Contains,
            _ => fallback,
        };

    /// <summary>行高夹紧到 16–48（坏值回默认 24）。</summary>
    public static double ClampRowHeight(double value)
        => double.IsNaN(value) || double.IsInfinity(value) || value <= 0
            ? DefaultRowHeight
            : Math.Clamp(value, MinRowHeight, MaxRowHeight);

    public static string FormatRowHeight(double value)
        => ClampRowHeight(value).ToString("0.##", CultureInfo.InvariantCulture);

    public static double ParseRowHeight(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? ClampRowHeight(value)
            : DefaultRowHeight;

    public static int ClampSearchDebounceRows(int value) => Math.Clamp(value, MinSearchDebounceRows, MaxSearchDebounceRows);

    public static int ClampSearchDebounceMs(int value) => Math.Clamp(value, MinSearchDebounceMs, MaxSearchDebounceMs);

    public static int ParseSearchDebounceRows(string? raw)
        => int.TryParse(raw?.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? ClampSearchDebounceRows(value)
            : DefaultSearchDebounceRows;

    public static int ParseSearchDebounceMs(string? raw)
        => int.TryParse(raw?.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? ClampSearchDebounceMs(value)
            : DefaultSearchDebounceMs;

    /// <summary>数值列（体积/文件大小）默认降序，文本列默认升序（2026-11-01 用户定）。</summary>
    public static bool DefaultDescending(LibrarySortField field)
        => field is LibrarySortField.AppSize or LibrarySortField.FileSize;

    /// <summary>
    /// 点列头后的方向（2026-11-01 抽出为纯函数，便于单测；审查报告空洞 #2）：
    /// 同列再点 → 反向；换列 → 该列默认方向（数值降序 / 文本升序）。
    /// </summary>
    public static bool NextDescending(LibrarySort current, LibrarySortField field)
        => current.Field == field ? !current.Descending : DefaultDescending(field);
}
