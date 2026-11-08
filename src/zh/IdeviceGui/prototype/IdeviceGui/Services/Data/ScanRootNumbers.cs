using System.IO;
using System.Text.Json;

namespace IdeviceGui.Services.Data;

/// <summary>
/// 扫描根的**显示编号**（`Root1`、`Root2`…，2026-11-02 用户定）。
/// <para>
/// - 编号**不重排**：删掉 Root2 后 Root3 仍是 Root3；下次新增**补最小空号**（补回 Root2）。<br/>
/// - 存 `Settings` 键 <see cref="SettingsKey"/>（JSON：绝对路径 → 编号），**不动 `ScanRoots` 表结构**（不做 schema 迁移）；<br/>
/// - 路径比较用规范化后的绝对路径（大小写不敏感），与 <see cref="ScanRootStore.Normalize"/> 一致。
/// </para>
/// </summary>
internal static class ScanRootNumbers
{
    public const string SettingsKey = "Scan.RootNumbers";

    /// <summary>读映射（坏 JSON → 空表）。</summary>
    public static Dictionary<string, int> Load(AppDatabase db) => Parse(SettingsStore.GetString(db, SettingsKey));

    /// <summary>解析持久化值：只收 `路径 → 正整数`，坏项丢弃。</summary>
    public static Dictionary<string, int> Parse(string? json)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return map;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var item in doc.RootElement.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Number) continue;
                if (!item.Value.TryGetInt32(out var number) || number < 1) continue;

                var key = Normalize(item.Name);
                if (key.Length == 0) continue;
                map[key] = number;
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    public static string Format(IReadOnlyDictionary<string, int> map)
        => JsonSerializer.Serialize(map.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 确保每个根都有编号：缺的按传入顺序（`ScanRoots` 按 Id，即添加顺序）取**最小空号**。
    /// 有变化才写库。返回「路径 → 编号」。
    /// </summary>
    public static Dictionary<string, int> Ensure(AppDatabase db, IReadOnlyList<ScanRootRow> roots)
    {
        var map = Load(db);
        var changed = false;

        // 去掉已不存在的根（删除根的入口会主动 Remove，这里再兜一次）
        var alive = new HashSet<string>(roots.Select(r => Normalize(r.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var key in map.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            map.Remove(key);
            changed = true;
        }

        foreach (var root in roots)
        {
            var key = Normalize(root.Path);
            if (key.Length == 0 || map.ContainsKey(key)) continue;
            map[key] = NextFree(map.Values);
            changed = true;
        }

        if (changed) SettingsStore.SetString(db, SettingsKey, Format(map));
        return map;
    }

    /// <summary>删掉某个根的编号（删根时调；编号从此空出，下次新增补回）。</summary>
    public static void Remove(AppDatabase db, string path)
    {
        var map = Load(db);
        if (!map.Remove(Normalize(path))) return;
        SettingsStore.SetString(db, SettingsKey, Format(map));
    }

    /// <summary>最小可用编号（1 起，跳过已用）。</summary>
    public static int NextFree(IEnumerable<int> used)
    {
        var set = used.ToHashSet();
        var n = 1;
        while (set.Contains(n)) n++;
        return n;
    }

    /// <summary>显示名：`Root{n}`。</summary>
    public static string Label(int number) => "Root" + number;

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch (Exception)
        {
            return "";
        }
    }
}
