using System.Globalization;
using System.Xml.Linq;

namespace IdeviceGui.Services.Diagnostics;

/// <summary>
/// 实时电量/充电状态（来源：<c>ideviceinfo -q com.apple.mobile.battery</c>）。
/// iOS 4–8 实测都有 <c>BatteryCurrentCapacity</c>，与 <c>GasGaugeCapability</c> 无关（iPod touch 该位为 false 但电量照常）。
/// </summary>
internal readonly record struct BatteryUsage(int? Percent, bool Charging, bool FullyCharged)
{
    public static BatteryUsage Unknown => new(null, false, false);

    public bool HasValue => Percent is not null;

    /// <summary>界面用：<c>90%（充电中）</c> / <c>100%（已充满）</c> / <c>100%</c> / <c>—</c>。</summary>
    public string Display
    {
        get
        {
            if (Percent is not { } pct) return "—";
            if (Charging) return $"{pct}%（充电中）";
            if (FullyCharged) return $"{pct}%（已充满）";
            return $"{pct}%";
        }
    }
}

/// <summary>
/// 电池健康（来源：<c>idevicediagnostics -u &lt;UDID&gt; diagnostics GasGauge</c>）。
/// 健康度 = FullChargeCapacity / DesignCapacity；≥100% 判"不可信"（用户 2026-10-31 拍板，实测 iPod touch 5 = 101.9%）。
/// </summary>
internal readonly record struct BatteryHealth(int? DesignMah, int? FullChargeMah, int? CycleCount)
{
    public static BatteryHealth Unknown => new(null, null, null);

    public bool HasValue => DesignMah is > 0 && FullChargeMah is not null;

    /// <summary>健康度比例（可能 &gt; 1，也可能 &lt; 1）；取不到返回 null。</summary>
    public double? Ratio => HasValue ? (double)FullChargeMah!.Value / DesignMah!.Value : null;

    /// <summary>≥ 100%（含恰好 100% 与 &gt;100%）→ 数据不可信。</summary>
    public bool Unreliable => Ratio is >= 1.0;

    /// <summary>界面用：<c>92.5%</c> / <c>101.9%(不可信)</c> / <c>—</c>。</summary>
    public string Display
    {
        get
        {
            if (Ratio is not { } ratio) return "—";
            var pct = (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture);
            return Unreliable ? $"{pct}%(不可信)" : $"{pct}%";
        }
    }

    /// <summary>ToolTip 用：<c>健康度 92.5%（1323 / 1430 mAh）· 循环 394 次</c>。</summary>
    public string ToolTip
    {
        get
        {
            if (Ratio is not { } ratio) return "健康度未知";
            var pct = (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture);
            var cycles = CycleCount is { } c ? $" · 循环 {c} 次" : "";
            var warn = Unreliable ? "（数据可能不可信）" : "";
            return $"健康度 {pct}%（{FullChargeMah} / {DesignMah} mAh）{cycles}{warn}";
        }
    }
}

/// <summary>
/// 电池硬件信息（来源：<c>idevicediagnostics -u &lt;UDID&gt; ioregentry AppleARMPMUCharger</c>，**仅 iOS 8+ 有该节点**）。
/// 实测 iPod touch 5 / iOS 8.4.1：Manufacturer=Y、Serial=YS43034240YDA21030、Temperature=2724、Voltage=3994。
/// </summary>
internal readonly record struct BatterySpec(
    string Manufacturer,
    string Serial,
    double? TemperatureC,
    double? VoltageV)
{
    public static BatterySpec Unknown => new("", "", null, null);

    public bool HasValue => Manufacturer.Length > 0 || Serial.Length > 0;

    public string TemperatureDisplay =>
        TemperatureC is { } t ? t.ToString("0.0", CultureInfo.InvariantCulture) + " ℃" : "—";

    public string VoltageDisplay =>
        VoltageV is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) + " V" : "—";
}

/// <summary>
/// 解析电池相关的三个只读命令输出。全部是"解析失败就返回 Unknown"的纯函数，便于单测（fixtures/battery 是 4 台真机原文）。
/// </summary>
internal static class BatteryInfoReader
{
    /// <summary>解析 <c>ideviceinfo -q com.apple.mobile.battery</c> 的 <c>Key: Value</c> 行输出。</summary>
    public static BatteryUsage ParseUsage(string? stdout)
    {
        var map = ParseKeyValues(stdout);
        int? percent = map.TryGetValue("BatteryCurrentCapacity", out var cap)
            && int.TryParse(cap.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
                ? p
                : null;
        var charging = map.TryGetValue("BatteryIsCharging", out var ch) && IsTrue(ch);
        var full = map.TryGetValue("FullyCharged", out var fc) && IsTrue(fc);
        return new BatteryUsage(percent, charging, full);
    }

    /// <summary>解析 <c>diagnostics GasGauge</c> 的 plist（字段在 <c>GasGauge</c> 子字典里，不是根）。</summary>
    public static BatteryHealth ParseGasGauge(string? stdout)
    {
        var dict = FindGasGaugeDict(stdout);
        if (dict is null) return BatteryHealth.Unknown;

        return new BatteryHealth(
            ReadInt(dict, "DesignCapacity"),
            ReadInt(dict, "FullChargeCapacity"),
            ReadInt(dict, "CycleCount"));
    }

    /// <summary>
    /// 解析 <c>ioregentry AppleARMPMUCharger</c> 的 plist。
    /// 实测结构是三层：根 dict → <c>IORegistry</c> → <c>PMUConfiguration</c> → 字段（Manufacturer / Serial / Temperature / Voltage）；
    /// 为兼容其他布局，找不到时回退到 <c>IORegistry</c> 字典、再回退到根字典。
    /// </summary>
    public static BatterySpec ParseChargerEntry(string? stdout)
    {
        var root = TryParsePlistDict(stdout);
        if (root is null) return BatterySpec.Unknown;

        var fields = FindChildDict(root, "IORegistry") is { } io
            ? FindChildDict(io, "PMUConfiguration") ?? io
            : root;

        var manufacturer = ReadString(fields, "Manufacturer");
        var serial = ReadString(fields, "Serial");

        // IORegistry 里温度单位是 0.01 ℃（2724 → 27.24 ℃）、电压单位是 mV（3994 → 3.994 V）。
        var temperature = ReadInt(fields, "Temperature") is { } t ? t / 100d : (double?)null;
        var voltage = ReadInt(fields, "Voltage") is { } v ? v / 1000d : (double?)null;

        return new BatterySpec(manufacturer, serial, temperature, voltage);
    }

    // ── 内部工具 ────────────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseKeyValues(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Length == 0) continue;
            map[key] = value;
        }

        return map;
    }

    private static bool IsTrue(string raw) =>
        raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Trim() == "1";

    /// <summary>把 plist 文本解析成根 <c>&lt;dict&gt;</c>；失败（空、错误文本、非 plist）返回 null。</summary>
    private static XElement? TryParsePlistDict(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (!trimmed.StartsWith("<?xml", StringComparison.Ordinal) && !trimmed.StartsWith("<plist", StringComparison.Ordinal))
            return null;

        try
        {
            var doc = XDocument.Parse(trimmed);
            var dict = doc.Root?.Element("dict");
            return dict;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>取 <c>GasGauge</c> 键后面的子字典（结构：dict→key GasGauge→dict）。</summary>
    private static XElement? FindGasGaugeDict(string? text) =>
        TryParsePlistDict(text) is { } root ? FindChildDict(root, "GasGauge") : null;

    /// <summary>在字典里找某个键后面的子字典（不是子字典则返回 null）。</summary>
    private static XElement? FindChildDict(XElement dict, string key)
    {
        foreach (var k in dict.Elements("key"))
        {
            if (!string.Equals(k.Value.Trim(), key, StringComparison.Ordinal)) continue;
            var next = k.ElementsAfterSelf().FirstOrDefault();
            return next?.Name == "dict" ? next : null;
        }

        return null;
    }

    private static string ReadString(XElement dict, string key)
    {
        var value = ValueAfterKey(dict, key);
        return value?.Name == "string" ? value.Value.Trim() : "";
    }

    private static int? ReadInt(XElement dict, string key)
    {
        var value = ValueAfterKey(dict, key);
        if (value is null) return null;
        if (value.Name != "integer") return null;

        return int.TryParse(value.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static XElement? ValueAfterKey(XElement dict, string key)
    {
        foreach (var k in dict.Elements("key"))
        {
            if (!string.Equals(k.Value.Trim(), key, StringComparison.Ordinal)) continue;
            return k.ElementsAfterSelf().FirstOrDefault();
        }

        return null;
    }
}
