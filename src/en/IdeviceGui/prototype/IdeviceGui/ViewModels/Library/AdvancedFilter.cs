using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdeviceGui.ViewModels.Library;

internal enum AdvancedFilterColumn
{
    Name,
    BundleId,
    FilePath,
    Version,
    MinOs,
    AppSizeMb,
    FileSizeMb,
    DeviceFamily,
    Origin,
    Arch,
    State,
    Compatibility,
}

internal enum FilterJoin
{
    And,
    Or,
}

internal enum TextFilterMode
{
    Exact,
    Contains,
}

/// <summary>高级筛选的一个条件（2026-11-05 用户定）。</summary>
internal sealed class AdvancedFilterCondition
{
    public AdvancedFilterColumn Column { get; set; } = AdvancedFilterColumn.Name;
    public bool Enabled { get; set; } = true;
    public TextFilterMode TextMode { get; set; } = TextFilterMode.Exact;
    public string Text { get; set; } = "";
    public string Min { get; set; } = "";
    public string Max { get; set; } = "";
    public List<string> Selected { get; set; } = new();
    public CompatFilterMode Compatibility { get; set; } = CompatFilterMode.Installable;

    /// <summary>设备族条件：iPhone 侧 / iPad 侧（各自可选“无”），用 ` + ` 拼成一个条件值。</summary>
    public string PhoneSide { get; set; } = "";
    public string PadSide { get; set; } = "";

    public AdvancedFilterCondition Clone() => new()
    {
        Column = Column,
        Enabled = Enabled,
        TextMode = TextMode,
        Text = Text,
        Min = Min,
        Max = Max,
        Selected = Selected.ToList(),
        Compatibility = Compatibility,
        PhoneSide = PhoneSide,
        PadSide = PadSide,
    };

    public bool IsActive => Enabled && !IsBlank;

    internal bool IsBlank => Column switch
    {
        AdvancedFilterColumn.Name or AdvancedFilterColumn.BundleId or AdvancedFilterColumn.FilePath
            => string.IsNullOrWhiteSpace(Text),
        AdvancedFilterColumn.Version or AdvancedFilterColumn.MinOs
            => string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Min) && string.IsNullOrWhiteSpace(Max),
        AdvancedFilterColumn.AppSizeMb or AdvancedFilterColumn.FileSizeMb
            => string.IsNullOrWhiteSpace(Min) && string.IsNullOrWhiteSpace(Max),
        AdvancedFilterColumn.DeviceFamily
            => string.IsNullOrWhiteSpace(PhoneSide) && string.IsNullOrWhiteSpace(PadSide),
        AdvancedFilterColumn.Origin
            => Selected.Count == 0,
        AdvancedFilterColumn.Arch or AdvancedFilterColumn.State
            => Selected.Count == 0,
        AdvancedFilterColumn.Compatibility
            => Compatibility == CompatFilterMode.Off,
        _ => false,
    };
}

internal sealed class AdvancedFilterGroup
{
    public FilterJoin Join { get; set; } = FilterJoin.And;
    public List<AdvancedFilterCondition> Conditions { get; set; } = new();

    public AdvancedFilterGroup Clone() => new()
    {
        Join = Join,
        Conditions = Conditions.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>
/// 高级筛选状态（2026-11-05）：组内统一 AND/OR、组间 AND、列不能重复；
/// 全部条件与搜索框、兼容性下拉框之间 AND。
/// </summary>
internal sealed class AdvancedFilterState
{
    public bool Enabled { get; set; }
    public List<AdvancedFilterGroup> Groups { get; set; } = new();

    /// <summary>筛选条件版本（变更时递增，用于 LibraryView 的过滤缓存失效）。</summary>
    public long Version { get; set; }

    public int ConditionCount => Enabled
        ? Groups.Sum(g => g.Conditions.Count(c => c.IsActive))
        : 0;

    public AdvancedFilterState Clone() => new()
    {
        Enabled = Enabled,
        Version = Version,
        Groups = Groups.Select(g => g.Clone()).ToList(),
    };

    public void Touch() => Version++;

    public bool Matches(LibraryRow row, string? productType, string? deviceVersion, string? hardwareModel)
    {
        if (!Enabled) return true;

        foreach (var group in Groups)
        {
            var conditions = group.Conditions.Where(c => c.IsActive).ToList();
            if (conditions.Count == 0) continue;

            var all = true;
            var any = false;
            foreach (var condition in conditions)
            {
                var matched = Match(condition, row, productType, deviceVersion, hardwareModel);
                any |= matched;
                all &= matched;
            }

            if (group.Join == FilterJoin.And ? !all : !any) return false;
        }

        return true;
    }

    private static bool Match(AdvancedFilterCondition condition, LibraryRow row,
        string? productType, string? deviceVersion, string? hardwareModel)
    {
        switch (condition.Column)
        {
            case AdvancedFilterColumn.Name:
                return MatchText(row.Name, condition);
            case AdvancedFilterColumn.BundleId:
                return MatchText(row.BundleId, condition);
            case AdvancedFilterColumn.FilePath:
                return MatchText(row.FilePath, condition);
            case AdvancedFilterColumn.Version:
                return MatchVersionText(row.Version, condition);
            case AdvancedFilterColumn.MinOs:
                return MatchVersionText(row.MinOs, condition);
            case AdvancedFilterColumn.AppSizeMb:
                return MatchMb(row.AppSize, condition);
            case AdvancedFilterColumn.FileSizeMb:
                return MatchMb(row.FileSize, condition);
            case AdvancedFilterColumn.DeviceFamily:
                return MatchDeviceFamily(row.DeviceFamilyText, condition);
            case AdvancedFilterColumn.Origin:
                return condition.Selected.Contains(row.OriginText, StringComparer.Ordinal);
            case AdvancedFilterColumn.Arch:
                return condition.Selected.Contains(row.ArchText, StringComparer.Ordinal);
            case AdvancedFilterColumn.State:
                return condition.Selected.Contains(row.StateText, StringComparer.Ordinal);
            case AdvancedFilterColumn.Compatibility:
                return DeviceCompat.Pass(row, condition.Compatibility, productType, deviceVersion, hardwareModel);
            default:
                return true;
        }
    }

    private static bool MatchText(string? value, AdvancedFilterCondition condition)
    {
        var actual = value ?? "";
        var expected = condition.Text ?? "";
        return condition.TextMode == TextFilterMode.Exact
            ? string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            : actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchVersionText(string? value, AdvancedFilterCondition condition)
    {
        if (string.IsNullOrWhiteSpace(value) || !DeviceCompat.TryParse(value, out var actual)) return false;

        var hasRange = !string.IsNullOrWhiteSpace(condition.Min) || !string.IsNullOrWhiteSpace(condition.Max);
        if (hasRange)
        {
            if (!string.IsNullOrWhiteSpace(condition.Min) &&
                (!DeviceCompat.TryParse(condition.Min, out var min) || DeviceCompat.Compare(actual, min) < 0))
                return false;
            if (!string.IsNullOrWhiteSpace(condition.Max) &&
                (!DeviceCompat.TryParse(condition.Max, out var max) || DeviceCompat.Compare(actual, max) > 0))
                return false;
            return true;
        }

        if (string.IsNullOrWhiteSpace(condition.Text)) return true;
        return DeviceCompat.TryParse(condition.Text, out var exact) && DeviceCompat.Compare(actual, exact) == 0;
    }

    private static bool MatchMb(long bytes, AdvancedFilterCondition condition)
    {
        var mb = bytes / 1024d / 1024d;
        if (double.TryParse(condition.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && mb < min)
            return false;
        if (double.TryParse(condition.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && mb > max)
            return false;
        return true;
    }

    private static bool MatchDeviceFamily(string? actual, AdvancedFilterCondition condition)
    {
        var phone = condition.PhoneSide ?? "";
        var pad = condition.PadSide ?? "";
        var expected = phone.Length > 0 && pad.Length > 0 ? phone + " + " + pad
            : phone.Length > 0 ? phone
            : pad;

        if (expected.Length == 0) return true;

        var value = DeviceAdaptation.NormalizeDisplay(actual);
        var normalizedExpected = DeviceAdaptation.NormalizeDisplay(expected);
        // Universal Retina 是 4.7 + iPad Retina 的合并写法
        if (value == "Universal Retina" && normalizedExpected == "4.7\"/5.5\" + iPad Retina") return true;
        return string.Equals(value, normalizedExpected, StringComparison.Ordinal);
    }
}

internal static class AdvancedFilterCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AdvancedFilterState state) => JsonSerializer.Serialize(state, Options);

    public static AdvancedFilterState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AdvancedFilterState();
        try
        {
            return JsonSerializer.Deserialize<AdvancedFilterState>(json, Options) ?? new AdvancedFilterState();
        }
        catch
        {
            return new AdvancedFilterState();
        }
    }
}
