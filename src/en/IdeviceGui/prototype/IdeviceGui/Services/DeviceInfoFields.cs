using System.Text.Json;

namespace IdeviceGui.Services;

/// <summary>设备信息行的字段（M4-b4）。顺序与可见性都可配置，配置存 <c>Settings</c>（键 <see cref="DeviceInfoFields.SettingsKey"/>）。</summary>
internal enum DeviceInfoField
{
    Name,
    Product,
    Version,
    Capacity,
    Color,
    BatteryHealth,
    Ecid,
}

/// <summary>一个字段的配置：是否显示 + 显示顺序（小的在前）。</summary>
internal readonly record struct DeviceInfoFieldSetting(DeviceInfoField Field, bool Visible, int Order);

/// <summary>
/// 设备信息行的字段配置（2026-11-01 用户定：逐字段可开关 + 可排序；设置面板以后再做，本类先把接口留好）。
/// <para>默认集与顺序 = 名称 · 型号 · 版本 · 容量 · 颜色 · 电池健康 · ECID（ECID 放最后，窄窗口先被省略）。</para>
/// <para>解析规则：坏 JSON / 非数组 → 回默认；未知 id 丢弃；重复 id 只保留第一次；缺失字段按默认顺序补齐；order 相同按默认顺序稳定排序。</para>
/// </summary>
internal static class DeviceInfoFields
{
    public const string SettingsKey = "Device.InfoFields";

    /// <summary>默认配置（用户 2026-11-01 定稿）。</summary>
    public static IReadOnlyList<DeviceInfoFieldSetting> Default { get; } = BuildDefault();

    /// <summary>按配置取"要显示的字段，按 order 升序"。</summary>
    public static IReadOnlyList<DeviceInfoField> OrderedVisible(IReadOnlyList<DeviceInfoFieldSetting> settings) =>
        settings.Where(s => s.Visible)
                .OrderBy(s => s.Order)
                .ThenBy(s => DefaultOrder(s.Field))
                .Select(s => s.Field)
                .ToList();

    /// <summary>解析持久化值；任何坏数据都回默认（不允许因为配置坏掉而让信息行空着）。</summary>
    public static IReadOnlyList<DeviceInfoFieldSetting> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Default;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Default;

            var seen = new HashSet<DeviceInfoField>();
            var parsed = new List<DeviceInfoFieldSetting>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (id is null) continue;
                if (!TryParseField(id, out var field)) continue;
                if (!seen.Add(field)) continue;   // 重复 id 只保留第一次（顺序以出现的第一个为准）

                var visible = !item.TryGetProperty("visible", out var visEl)
                              || visEl.ValueKind != JsonValueKind.False;
                var order = item.TryGetProperty("order", out var ordEl) && ordEl.TryGetInt32(out var o)
                    ? o
                    : DefaultOrder(field);

                parsed.Add(new DeviceInfoFieldSetting(field, visible, order));
            }

            if (parsed.Count == 0) return Default;

            // 缺的字段按默认顺序补在末尾（老配置不会把新字段吃掉）
            foreach (var d in Default)
            {
                if (seen.Contains(d.Field)) continue;
                parsed.Add(d);
            }

            return parsed;
        }
    }

    /// <summary>序列化（字段 id 用稳定小写名，供设置面板与手改文档使用）。</summary>
    public static string Serialize(IReadOnlyList<DeviceInfoFieldSetting> settings)
    {
        var payload = settings
            .OrderBy(s => s.Order)
            .Select(s => new Dictionary<string, object>
            {
                ["id"] = ToId(s.Field),
                ["visible"] = s.Visible,
                ["order"] = s.Order,
            });

        return JsonSerializer.Serialize(payload);
    }

    public static string ToId(DeviceInfoField field) => field switch
    {
        DeviceInfoField.Name => "name",
        DeviceInfoField.Product => "product",
        DeviceInfoField.Version => "version",
        DeviceInfoField.Capacity => "capacity",
        DeviceInfoField.Color => "color",
        DeviceInfoField.BatteryHealth => "batteryHealth",
        DeviceInfoField.Ecid => "ecid",
        _ => "name",
    };

    public static bool TryParseField(string id, out DeviceInfoField field)
    {
        switch (id.Trim().ToLowerInvariant())
        {
            case "name": field = DeviceInfoField.Name; return true;
            case "product": field = DeviceInfoField.Product; return true;
            case "version": field = DeviceInfoField.Version; return true;
            case "capacity": field = DeviceInfoField.Capacity; return true;
            case "color": field = DeviceInfoField.Color; return true;
            case "batteryhealth": field = DeviceInfoField.BatteryHealth; return true;
            case "ecid": field = DeviceInfoField.Ecid; return true;
            default: field = DeviceInfoField.Name; return false;
        }
    }

    private static int DefaultOrder(DeviceInfoField field)
    {
        for (var i = 0; i < Default.Count; i++)
        {
            if (Default[i].Field == field) return Default[i].Order;
        }

        return int.MaxValue;
    }

    private static DeviceInfoFieldSetting[] BuildDefault() =>
    [
        new(DeviceInfoField.Name, true, 0),
        new(DeviceInfoField.Product, true, 1),
        new(DeviceInfoField.Version, true, 2),
        new(DeviceInfoField.Capacity, true, 3),
        new(DeviceInfoField.Color, true, 4),
        new(DeviceInfoField.BatteryHealth, true, 5),
        new(DeviceInfoField.Ecid, true, 6),
    ];
}
