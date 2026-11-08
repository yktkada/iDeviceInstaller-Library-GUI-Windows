namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// "与当前设备兼容"（2026-11-02 用户定）：**只看设备族 + 最低 iOS**，不做完整 M5 兼容判定。
/// - 设备族：包 DeviceFamily = Universal → 都行；含 iPad → 只能 iPad；含 iPhone/iPod 或 "1" → 非 iPad；含 "2" → iPad。
/// - **设备族为空 / `unknown` / 未识别 → 按早期 iPhone OG 3.5（320×480）处理：iPhone/iPod 放行，iPad 不放行**（2026-11-05 用户定）。
/// - iOS：包 MinOs ≤ 设备版本（数值逐段比较）；MinOs 空/坏值 → 放行。
/// 设备信息缺失（没有当前设备）仍放行，避免误藏。
/// </summary>
internal enum CompatFilterMode
{
    Off,
    Installable,
    Adapted,
}

internal static class DeviceCompat
{
    /// <summary>兼容性下拉框的统一入口（2026-11-05）。缺设备信息一律放行（宁可多显示）。</summary>
    public static bool Pass(LibraryRow row, CompatFilterMode mode, string? productType, string? deviceVersion,
        string? hardwareModel)
    {
        if (mode == CompatFilterMode.Off) return true;
        if (string.IsNullOrWhiteSpace(productType) || string.IsNullOrWhiteSpace(deviceVersion)) return true;

        if (IsUnknownFamily(row)) return !IsIpad(productType);   // 早期 iPhone OG 3.5（320×480）按 iPhone 兼容
        if (!IsFamilyCompatible(row.DeviceFamily, productType)) return false;
        if (!IsMinOsCompatible(row.MinOs, deviceVersion)) return false;
        if (mode == CompatFilterMode.Installable) return true;

        var (tier, _) = DeviceScreen.Resolve(hardwareModel, productType);
        return tier is null || DeviceAdaptation.IsAdapted(row.DeviceFamilyText, tier.Value);
    }

    public static bool IsCompatible(LibraryRow row, string? productType, string? deviceVersion)
    {
        if (string.IsNullOrWhiteSpace(productType) || string.IsNullOrWhiteSpace(deviceVersion)) return true;
        if (IsUnknownFamily(row)) return !IsIpad(productType);
        if (!IsFamilyCompatible(row.DeviceFamily, productType)) return false;
        return IsMinOsCompatible(row.MinOs, deviceVersion);
    }

    /// <summary>显示为 unknown 的包：极早期 iPhone OG 3.5（320×480）——只按 iPhone 兼容，iPad 不放行。</summary>
    private static bool IsUnknownFamily(LibraryRow row)
        => string.Equals((row.DeviceFamilyText ?? "").Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsIpad(string? productType)
        => (productType ?? "").StartsWith("iPad", StringComparison.OrdinalIgnoreCase);

    private static bool IsFamilyCompatible(string? family, string productType)
    {
        var text = (family ?? "").Trim();
        if (text.Equals("Universal", StringComparison.OrdinalIgnoreCase)) return true;

        // 2026-11-05 用户定：unknown/空设备族不能证明可安装 → 不在「可安装」筛选里显示。
        if (text.Length == 0 || text.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return false;

        var isIpad = productType.StartsWith("iPad", StringComparison.OrdinalIgnoreCase);

        if (text.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return isIpad;
        if (text.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("iPod", StringComparison.OrdinalIgnoreCase)) return !isIpad;

        // UIDeviceFamily 数值形态："1"（iPhone/iPod touch）/ "2"（iPad）/ "1,2"
        var has1 = false;
        var has2 = false;
        foreach (var token in text.Split(new[] { ',', ';', '，', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "1") has1 = true;
            else if (token == "2") has2 = true;
        }

        if (has1 || has2) return isIpad ? has2 : has1;

        return false;   // 未识别的设备族：无法证明可安装，不放行
    }

    private static bool IsMinOsCompatible(string? minOs, string deviceVersion)
    {
        if (string.IsNullOrWhiteSpace(minOs)) return true;
        if (!TryParse(minOs, out var min)) return true;
        if (!TryParse(deviceVersion, out var device)) return true;
        return Compare(device, min) >= 0;
    }

    internal static bool TryParse(string text, out int[] parts)
    {
        parts = Array.Empty<int>();
        var cleaned = text.Trim().Trim('"');
        var tokens = cleaned.Split('.', '-', '_', ' ');
        var values = new List<int>();
        foreach (var token in tokens)
        {
            var digits = new string(token.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) continue;
            if (!int.TryParse(digits, out var value)) return false;
            values.Add(value);
        }

        if (values.Count == 0) return false;
        parts = values.ToArray();
        return true;
    }

    internal static int Compare(int[] left, int[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        return 0;
    }
}
