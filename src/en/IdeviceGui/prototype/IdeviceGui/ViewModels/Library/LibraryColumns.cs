using System.Globalization;
using System.Text.Json;
using IdeviceGui.Services;
using IdeviceGui.Services.Data;

namespace IdeviceGui.ViewModels.Library;

/// <summary>网格列描述（docs/06 §3.1）：`Key` 是**稳定标识**，不存"第几列"，加列/删列时旧配置仍能对上。</summary>
internal sealed record ColumnSpec(string Key, string Header, double DefaultWidth, double MinWidth = 60);

/// <summary>一列的持久化状态：显示顺序 = 数组顺序（docs/09 §3.2 `Grid.Columns`）。</summary>
internal sealed record GridColumnState(string Key, double Width, bool Visible);

/// <summary>
/// 从界面抓列状态时的中间结构：WPF 用户拖动换序只改 <c>DataGridColumn.DisplayIndex</c>，
/// **不改 <c>Columns</c> 集合顺序**，所以保存必须按 DisplayIndex 排（审核 P2-4）。
/// </summary>
internal readonly record struct GridColumnEntry(string Key, int DisplayIndex, double Width, bool Visible);

/// <summary>
/// 10 列的定义与 `Grid.Columns` 的读写（docs/06 §2/§3.1）。
/// 默认顺序（2026-10-31 用户拍板）：图标 · 名称 · 解压后体积· 版本 · 最低 iOS · 设备族 · BundleId · 架构 · 原始大小 · 状态（最后）。
/// <para>
/// **列序版本（2026-11-01）**：老版本存下来的 `Grid.Columns` 是旧顺序（`name` 开头、`icon` 第 5、`state` 第 3），
/// 直接读会让"图标不是第一列、状态不在最后"。用 `Grid.Columns.Version` 做一次迁移：旧配置**保留宽度/可见性**，
/// 只把顺序重排成当前默认；迁移后写回版本号，之后用户自己拖的顺序不再被动。
/// </para>
/// </summary>
internal static class LibraryColumns
{
    public const string GridColumnsKey = "Grid.Columns";

    /// <summary>列序版本：2 = 图标第一、状态最后（2026-11-01 起）。老配置（无版本号）读到时会迁移一次。</summary>
    public const string GridColumnsVersionKey = "Grid.Columns.Version";

    public const int LayoutVersion = 7;   // 7 = 新增「来源」列（2026-11-05）；6 = 设备族列加宽到 130（2026-11-04 M5-lite）；5 = 勾列宽度砍半（2026-11-02）；
                                          // 2026-11-03 起迁移**不再重排列顺序**（审查 B1）

    /// <summary>已安装标记列（2026-11-01：最前、极窄、✓/空）。</summary>
    public const string Installed = "installed";

    public const string Icon = "icon";
    public const string Name = "name";
    public const string Origin = "origin";
    public const string AppSize = "size";
    public const string Version = "version";
    public const string MinOs = "minOs";
    public const string DeviceFamily = "family";
    public const string BundleId = "bundleId";
    public const string Arch = "arch";
    public const string FileSize = "fileSize";
    public const string State = "state";

    /// <summary>文件路径列（2026-11-01：最后一列）。</summary>
    public const string FilePath = "filePath";

    /// <summary>按界面显示顺序（DisplayIndex）产出要保存的列状态；同下标时保持传入顺序。</summary>
    public static IReadOnlyList<GridColumnState> OrderForSave(IEnumerable<GridColumnEntry> entries) =>
        entries.OrderBy(entry => entry.DisplayIndex)
               .Select(entry => new GridColumnState(entry.Key, entry.Width, entry.Visible))
               .ToList();

    public static IReadOnlyList<ColumnSpec> Defaults { get; } = new[]
    {
        new ColumnSpec(Installed, "✓", 13, 11),   // 2026-11-02 用户定：宽度砍半
        new ColumnSpec(Icon, "Icons", 44, 40),
        new ColumnSpec(Name, "Name", 170, 120),
        new ColumnSpec(Origin, "Source", 110, 90),
        new ColumnSpec(AppSize, "Uncompressed size", 132, 57),
        new ColumnSpec(Version, "Version", 74, 36),
        new ColumnSpec(MinOs, "iOS", 74, 36),
        new ColumnSpec(DeviceFamily, "Device family", 130, 66),
        new ColumnSpec(BundleId, "BundleId", 150, 100),
        new ColumnSpec(Arch, "Architecture", 86, 70),
        new ColumnSpec(FileSize, "Original size", 84, 57),
        new ColumnSpec(State, "Status", 120, 80),
        new ColumnSpec(FilePath, "File path", 220, 120),
    };

    public static IReadOnlyList<GridColumnState> DefaultStates =>
        Defaults.Select(spec => new GridColumnState(spec.Key, spec.DefaultWidth, true)).ToArray();

    public static ColumnSpec? Find(string key) => Defaults.FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// 列显示/隐藏接口（M4-b5，2026-11-04）：按稳定 key 改一列的可见性，**保留顺序与宽度**；
    /// 未知 key 原样返回（设置面板以后直接用这个函数，不再各处手改 JSON）。
    /// </summary>
    public static IReadOnlyList<GridColumnState> SetVisible(
        IReadOnlyList<GridColumnState> states, string key, bool visible)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (Find(key) is null) return states.ToList();
        return states
            .Select(state => string.Equals(state.Key, key, StringComparison.Ordinal)
                ? state with { Visible = visible }
                : state)
            .ToList();
    }

    /// <summary>
    /// 读配置；库没打开 / 没写过 / 坏值 → 默认顺序与宽度。
    /// 老版本配置（无版本号，或版本 &lt; <see cref="LayoutVersion"/>）只做两件事：
    /// **补上新增列**（按默认顺序插到合适位置）+ 按版本改个别列的宽度，
    /// **绝不重排用户拖过的列顺序**（审查 B1，2026-11-03 修）；最后把版本号写回。
    /// </summary>
    public static IReadOnlyList<GridColumnState> Load(AppDatabase? db)
    {
        if (db is null) return DefaultStates;
        try
        {
            var raw = SettingsStore.GetString(db, GridColumnsKey);
            if (string.IsNullOrWhiteSpace(raw)) return DefaultStates;

            var states = ParseSaved(raw);   // 审查 B1：迁移要看"用户真实保存的顺序"，不能用 Parse 的"缺列补最后"
            var fromVersion = ReadVersion(db);
            if (fromVersion >= LayoutVersion) return Parse(raw);   // 已是最新版本：保持原语义（缺列补最后）

            // 审查 B1：不能整体重排（会把用户拖过的列顺序冲掉）；只补缺列、保留已存顺序
            var migrated = EnsureKnownColumns(states);

            // v6：设备族列加宽到 130（M5-lite 显示串更长；只改这一列）
            if (fromVersion < 6)
            {
                migrated = migrated
                    .Select(state => state.Key == DeviceFamily
                        ? state with { Width = Defaults.First(s => s.Key == DeviceFamily).DefaultWidth }
                        : state)
                    .ToList();
            }

            // v5：勾列宽度砍半（只改这一列，其它列保留用户宽度）
            if (fromVersion < 5)
            {
                migrated = migrated
                    .Select(state => state.Key == Installed
                        ? state with { Width = Defaults.First(s => s.Key == Installed).DefaultWidth }
                        : state)
                    .ToList();
            }
            SettingsStore.SetString(db, GridColumnsKey, Serialize(migrated));
            SettingsStore.SetString(db, GridColumnsVersionKey, LayoutVersion.ToString(CultureInfo.InvariantCulture));
            return migrated;
        }
        catch
        {
            return DefaultStates;
        }
    }

    /// <summary>
    /// 迁移用：**保留用户已保存的列顺序**（审查 B1，2026-11-03）——丢掉未知 key、去重，
    /// 再把缺失的已知列按默认顺序插到合适位置（新增列不会把老列挤乱）。
    /// </summary>
    internal static IReadOnlyList<GridColumnState> EnsureKnownColumns(IReadOnlyList<GridColumnState> states)
    {
        var known = Defaults.Select(spec => spec.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GridColumnState>(Defaults.Count);
        foreach (var state in states)
        {
            if (!known.Contains(state.Key) || !seen.Add(state.Key)) continue;
            result.Add(state);
        }

        foreach (var spec in Defaults)
        {
            if (seen.Contains(spec.Key)) continue;

            var specIndex = IndexOfDefault(spec.Key);
            var insertAt = result.Count;
            for (var i = 0; i < result.Count; i++)
            {
                if (IndexOfDefault(result[i].Key) > specIndex)
                {
                    insertAt = i;
                    break;
                }
            }

            result.Insert(insertAt, new GridColumnState(spec.Key, spec.DefaultWidth, true));
            seen.Add(spec.Key);
        }

        return result;
    }

    private static int IndexOfDefault(string key)
    {
        for (var i = 0; i < Defaults.Count; i++)
        {
            if (string.Equals(Defaults[i].Key, key, StringComparison.Ordinal)) return i;
        }

        return int.MaxValue;
    }

    /// <summary>把一组列状态按当前默认顺序重排（保留宽度/可见性；认不出的 key 丢弃）。</summary>
    internal static IReadOnlyList<GridColumnState> CanonicalOrder(IReadOnlyList<GridColumnState> states)
    {
        var byKey = states
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var ordered = new List<GridColumnState>(Defaults.Count);
        foreach (var spec in Defaults)
        {
            if (byKey.TryGetValue(spec.Key, out var state)) ordered.Add(state);
        }

        return ordered;
    }

    private static int ReadVersion(AppDatabase db)
    {
        var raw = SettingsStore.GetString(db, GridColumnsVersionKey);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 0;
    }

    /// <summary>写配置（关窗时存一次）。失败只记文件日志，不影响关窗。</summary>
    public static void Save(AppDatabase? db, IReadOnlyList<GridColumnState> states, Action<string>? log = null)
    {
        if (db is null) return;
        try
        {
            SettingsStore.SetString(db, GridColumnsKey, Serialize(states));
            SettingsStore.SetString(db, GridColumnsVersionKey, LayoutVersion.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            try { log?.Invoke($"Failed to save column layout: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    internal static string Serialize(IReadOnlyList<GridColumnState> states)
    {
        var items = states.Select(s => new ColumnDto
        {
            key = s.Key,
            width = Math.Round(s.Width, 1),
            visible = s.Visible,
        });
        return JsonSerializer.Serialize(items.ToArray(), JsonOptions);
    }

    /// <summary>
    /// 解析配置：认不出的 key 丢掉、缺的 key 按默认顺序补在后面（保证列齐全）；
    /// 宽度夹到 [MinWidth, 600]；重复 key 只留第一个。
    /// </summary>
    internal static IReadOnlyList<GridColumnState> Parse(string? raw) => ParseCore(raw, includeMissing: true);

    /// <summary>
    /// 只解析**用户实际保存的列**（不补缺列）——迁移时用：先拿到真实顺序，再交给
    /// <see cref="EnsureKnownColumns"/> 把缺的新列插到正确位置（审查 B1，2026-11-03）。
    /// </summary>
    internal static IReadOnlyList<GridColumnState> ParseSaved(string? raw) => ParseCore(raw, includeMissing: false);

    private static IReadOnlyList<GridColumnState> ParseCore(string? raw, bool includeMissing)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultStates;

        ColumnDto[]? items;
        try
        {
            items = JsonSerializer.Deserialize<ColumnDto[]>(raw, JsonOptions);
        }
        catch
        {
            return DefaultStates;
        }

        if (items is null || items.Length == 0) return DefaultStates;

        var result = new List<GridColumnState>(Defaults.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var key = item.key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (!seen.Add(key)) continue;
            var spec = Find(key);
            if (spec is null) continue;                 // 认不出的列（旧版本/拼错）忽略
            var width = double.IsNaN(item.width) || item.width <= 0 ? spec.DefaultWidth : item.width;
            result.Add(new GridColumnState(key, Math.Clamp(width, spec.MinWidth, 600), item.visible));
        }

        // 缺的列按默认顺序补在最后（新版本加的列不会因为旧配置而消失）
        if (includeMissing)
        {
            foreach (var spec in Defaults)
            {
                if (seen.Contains(spec.Key)) continue;
                result.Add(new GridColumnState(spec.Key, spec.DefaultWidth, true));
            }
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ColumnDto
    {
        public string? key { get; set; }
        public double width { get; set; }
        public bool visible { get; set; } = true;
    }
}
