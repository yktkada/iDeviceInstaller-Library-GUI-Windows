namespace IdeviceGui.ViewModels.Library;

/// <summary>设备屏幕档位（M5-lite 文案口径）。</summary>
public enum ScreenTier
{
    Og35 = 1,
    Retina35 = 2,
    Screen4 = 3,
    Screen47 = 4,
    Ipad = 11,
    IpadRetina = 12,
}

/// <summary>
/// 设备屏幕档位识别（2026-11-05）：**HardwareModel 为主、ProductType 验证**。
/// HardwareModel 表先覆盖现有设备池；未登记 → 回落 ProductType；两边都有且档位不同 → mismatch。
/// </summary>
internal static class DeviceScreen
{
    public static ScreenTier? FromHardwareModel(string? model)
    {
        // 硬性约定（2026-11-05 用户）：HardwareModel **必须严格匹配（区分大小写）**；
        // 不做 Trim / ToUpper / ToLower，也不忽略大小写；不匹配就当未登记，回落 ProductType。
        var value = model ?? "";
        if (MatchesHardware(value, "N90AP") || MatchesHardware(value, "N94AP"))
            return ScreenTier.Retina35;       // iPhone 4 / 4S
        if (MatchesHardware(value, "N41AP") || MatchesHardware(value, "N42AP") ||
            MatchesHardware(value, "N78AP"))
            return ScreenTier.Screen4;       // iPhone 5 / iPod touch 5
        if (MatchesHardware(value, "K93AP") || MatchesHardware(value, "K94AP") ||
            MatchesHardware(value, "K95AP") || MatchesHardware(value, "K96AP"))
            return ScreenTier.Ipad;          // iPad 2 系
        return null;
    }

    private static bool MatchesHardware(string value, string expected) =>
        string.Equals(value, expected, StringComparison.Ordinal);

    public static ScreenTier? FromProductType(string? productType)
    {
        var text = (productType ?? "").Trim();
        if (text.Length == 0) return null;

        if (text.StartsWith("iPad", StringComparison.OrdinalIgnoreCase))
        {
            var version = VersionOf(text, "iPad");
            if (version is null) return ScreenTier.Ipad;
            // iPad 1 / 2 / mini1 → 非 Retina；iPad 3+ / mini2+ → Retina
            return version <= 2 ? ScreenTier.Ipad : ScreenTier.IpadRetina;
        }

        if (text.StartsWith("iPod", StringComparison.OrdinalIgnoreCase))
        {
            var version = VersionOf(text, "iPod");
            return version is >= 5 ? ScreenTier.Screen4
                : version is >= 4 ? ScreenTier.Retina35
                : ScreenTier.Og35;
        }

        if (text.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            var version = VersionOf(text, "iPhone");
            if (version is null) return null;
            return version switch
            {
                1 or 2 => ScreenTier.Og35,
                3 or 4 => ScreenTier.Retina35,
                5 or 6 => ScreenTier.Screen4,
                _ => ScreenTier.Screen47,
            };
        }

        return null;
    }

    /// <summary>返回 (最终档位, 是否不一致, ProductType)；档位取 HardwareModel 优先。</summary>
    public static (ScreenTier? Tier, bool Mismatch) Resolve(string? hardwareModel, string? productType)
    {
        var byHardware = FromHardwareModel(hardwareModel);
        var byProduct = FromProductType(productType);
        if (byHardware is not null && byProduct is not null && byHardware != byProduct)
            return (byHardware, true);
        return (byHardware ?? byProduct, false);
    }

    /// <summary>
    /// 顶栏型号文案（2026-11-05 用户定）：`营销名(ProductType)`，例如 `iPod touch 5(iPad2,1)`。
    /// 营销名优先取 HardwareModel（严格大小写），未知再回落 ProductType；都未知用 fallback（设备名）。
    /// </summary>
    public static string ModelLabel(string? hardwareModel, string? productType, string? fallbackName = "")
    {
        var product = (productType ?? "").Trim();
        var model = HardwareName(hardwareModel) ?? ProductName(product);
        if (string.IsNullOrWhiteSpace(model)) model = fallbackName ?? "";
        if (string.IsNullOrWhiteSpace(model)) model = product;
        if (string.IsNullOrWhiteSpace(product)) return model;
        return $"{model}({product})";
    }

    private static string? HardwareName(string? model) => model switch
    {
        "N90AP" => "iPhone 4",
        "N94AP" => "iPhone 4S",
        "N41AP" or "N42AP" => "iPhone 5",
        "N78AP" => "iPod touch 5",
        "K93AP" or "K94AP" or "K95AP" or "K96AP" => "iPad 2",
        _ => null,
    };

    private static string? ProductName(string productType)
    {
        var text = (productType ?? "").Trim();
        if (text.Length == 0) return null;

        if (text.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            var v = VersionPair(text, "iPhone");
            if (v is null) return null;
            var (major, minor) = v.Value;
            return (major, minor) switch
            {
                (1, 1) => "iPhone",
                (1, 2) => "iPhone 3G",
                (2, 1) => "iPhone 3GS",
                (3, _) => "iPhone 4",
                (4, 1) => "iPhone 4S",
                (5, 1) or (5, 2) => "iPhone 5",
                (5, 3) or (5, 4) => "iPhone 5c",
                (6, _) => "iPhone 5s",
                (7, 2) => "iPhone 6",
                (7, 1) => "iPhone 6 Plus",
                (8, 1) => "iPhone 6s",
                (8, 2) => "iPhone 6s Plus",
                (9, 1) or (9, 3) => "iPhone 7",
                (9, 2) or (9, 4) => "iPhone 7 Plus",
                (10, 1) or (10, 4) => "iPhone 8",
                (10, 2) or (10, 5) => "iPhone 8 Plus",
                (12, 8) => "iPhone SE (第 2 代)",
                (14, 6) => "iPhone SE (第 3 代)",
                _ => null,
            };
        }

        if (text.StartsWith("iPad", StringComparison.OrdinalIgnoreCase))
        {
            var v = VersionPair(text, "iPad");
            if (v is null) return null;
            var (major, _) = v.Value;
            return major switch
            {
                1 => "iPad",
                2 => "iPad 2",
                3 => "iPad (第 3 代)",
                4 => "iPad Air",
                _ => null,
            };
        }

        if (text.StartsWith("iPod", StringComparison.OrdinalIgnoreCase))
        {
            var v = VersionPair(text, "iPod");
            if (v is null) return null;
            var (major, _) = v.Value;
            return major switch
            {
                1 => "iPod touch",
                2 => "iPod touch 2",
                3 => "iPod touch 3",
                4 => "iPod touch 4",
                5 => "iPod touch 5",
                _ => null,
            };
        }

        return null;
    }

    private static (int Major, int Minor)? VersionPair(string text, string prefix)
    {
        var rest = text[prefix.Length..];
        var parts = rest.Split(',');
        if (parts.Length < 2) return null;
        return int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? (major, minor)
            : null;
    }

    public static bool IsIpadProduct(string? productType) =>
        (productType ?? "").Trim().StartsWith("iPad", StringComparison.OrdinalIgnoreCase);

    public static int Rank(ScreenTier tier) => tier switch
    {
        ScreenTier.Og35 => 1,
        ScreenTier.Retina35 => 2,
        ScreenTier.Screen4 => 3,
        ScreenTier.Screen47 => 4,
        ScreenTier.Ipad => 1,
        ScreenTier.IpadRetina => 2,
        _ => 0,
    };

    private static int? VersionOf(string text, string prefix)
    {
        var rest = text[prefix.Length..];
        var token = rest.Split(',')[0].Trim();
        return int.TryParse(token, out var value) ? value : null;
    }
}
