using IdeviceGui.Services.Parsing;

namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// M5-lite「设备族最高适配识别」纯逻辑（2026-11-04 用户定稿，docs/06 §6.11）：
/// **取最高档**——任一证据指向更高档就判该档；有 family 声明时最低档兜底；
/// **不加问号**；唯一特殊值是缺 `UIDeviceFamily` 的 <c>unknown</c>。
/// </summary>
internal static class DeviceAdaptation
{
    public const string Unknown = "unknown";

    internal enum PhoneRank { None = 0, Og35 = 1, R35 = 2, P4 = 3, P47 = 4 }

    internal enum PadRank { None = 0, NonRetina = 1, Retina = 2 }

    /// <summary>把 R / Retina 两种文案归一成 Retina 形态（语义判定不依赖显示开关）。</summary>
    internal static string NormalizeDisplay(string? text)
    {
        var value = (text ?? "").Trim();
        if (value == "iPad R") return "iPad Retina";
        if (value == "Universal R") return "Universal Retina";
        if (value.EndsWith(" + iPad R", StringComparison.Ordinal))
            return value[..^1] + "Retina";
        return value;
    }

    /// <summary>把显示串拆成 iPhone / iPad 两侧档位（`Universal Retina`、`A + B` 都支持）。</summary>
    internal static (PhoneRank Phone, PadRank Pad) ParseDisplaySides(string? text)
    {
        var value = NormalizeDisplay(text);
        if (value.Length == 0 || value == Unknown) return (PhoneRank.None, PadRank.None);
        if (value == "Universal Retina") return (PhoneRank.P47, PadRank.Retina);

        var parts = value.Split(" + ", StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return (ParsePhone(parts[0]), ParsePad(parts[1]));
        }

        return value.StartsWith("iPad", StringComparison.Ordinal)
            ? (PhoneRank.None, ParsePad(value))
            : (ParsePhone(value), PadRank.None);
    }

    /// <summary>「已适配」：App 该侧最高档 ≥ 设备档位（至少支持）。</summary>
    internal static bool IsAdapted(string? display, ScreenTier deviceTier)
    {
        var (phone, pad) = ParseDisplaySides(display);
        if (deviceTier is ScreenTier.Ipad or ScreenTier.IpadRetina)
        {
            var padTarget = deviceTier == ScreenTier.IpadRetina ? 2 : 1;
            return pad != PadRank.None && (int)pad >= padTarget;
        }

        return phone != PhoneRank.None && (int)phone >= (int)DeviceScreen.Rank(deviceTier);
    }

    private static PhoneRank ParsePhone(string text) => text switch
    {
        "4.7\"/5.5\"" => PhoneRank.P47,
        "4\"" => PhoneRank.P4,
        "3.5\"" => PhoneRank.R35,
        "OG 3.5\"" => PhoneRank.Og35,
        _ => PhoneRank.None,
    };

    private static PadRank ParsePad(string text) => NormalizeDisplay(text) switch
    {
        "iPad Retina" => PadRank.Retina,
        "iPad" => PadRank.NonRetina,
        _ => PadRank.None,
    };

    private enum PhoneTier { Og, R35, P4, P47 }

    private enum PadTier { NonRetina, Retina }

    /// <summary>按原始 family + 证据 JSON 出显示串（`LibraryRow.DeviceFamilyText`）。</summary>
    public static string Describe(string? family, string? evidenceJson, bool useRetinaText = true)
    {
        var evidence = LaunchEvidenceCodec.Parse(evidenceJson);
        return (family ?? "").Trim() switch
        {
            "iPhone" => PhoneText(JudgePhone(evidence)),
            "iPad" => PadText(JudgePad(evidence), useRetinaText),
            "Universal" => UniversalText(evidence, useRetinaText),
            _ => Unknown,
        };
    }

    private static PhoneTier JudgePhone(LaunchEvidenceDto? evidence)
    {
        if (evidence is not null)
        {
            if (evidence.S) return PhoneTier.P47;
            foreach (var size in evidence.L)
            {
                if (size.Length < 2) continue;
                var (w, h) = (size[0], size[1]);
                if (Is47(w, h)) return PhoneTier.P47;
            }

            foreach (var size in evidence.L)
            {
                if (size.Length < 2) continue;
                var (w, h) = (size[0], size[1]);
                if (Is4Inch(w, h)) return PhoneTier.P4;
            }

            foreach (var size in evidence.L)
            {
                if (size.Length < 2) continue;
                if (size[0] == 640 && size[1] == 960) return PhoneTier.R35;
            }

            if (evidence.I.Contains(114) || evidence.I.Contains(120)) return PhoneTier.R35;
        }

        // 有 iPhone family 即必定支持 3.5" 基线（2009 之前的机型）
        return PhoneTier.Og;
    }

    private static PadTier JudgePad(LaunchEvidenceDto? evidence)
    {
        if (evidence is not null)
        {
            foreach (var size in evidence.L)
            {
                if (size.Length < 2) continue;
                var (w, h) = (size[0], size[1]);
                if ((w == 1536 && h == 2048) || (w == 2048 && h == 1536)) return PadTier.Retina;
            }

            if (evidence.I.Contains(144) || evidence.I.Contains(152)) return PadTier.Retina;
        }

        // 有 iPad family 即必定支持 iPad 1/2 基线（1024×768）
        return PadTier.NonRetina;
    }

    private static bool Is47(int w, int h) =>
        (w == 375 && h == 667) || (w == 414 && h == 736) ||
        (w == 750 && h == 1334) || (w == 1242 && h == 2208) || (w == 2208 && h == 1242) ||
        (w == 667 && h == 375) || (h == 736 && w == 414);

    private static bool Is4Inch(int w, int h) =>
        (w == 320 && h == 568) || (w == 568 && h == 320) || (w == 640 && h == 1136) || (w == 1136 && h == 640);

    private static string PhoneText(PhoneTier tier) => tier switch
    {
        PhoneTier.P47 => "4.7\"/5.5\"",
        PhoneTier.P4 => "4\"",
        PhoneTier.R35 => "3.5\"",
        _ => "OG 3.5\"",
    };

    private static string PadText(PadTier tier, bool useRetinaText) =>
        tier == PadTier.Retina ? (useRetinaText ? "iPad Retina" : "iPad R") : "iPad";

    private static string UniversalText(LaunchEvidenceDto? evidence, bool useRetinaText)
    {
        var phone = JudgePhone(evidence);
        var pad = JudgePad(evidence);
        if (phone == PhoneTier.P47 && pad == PadTier.Retina)
            return useRetinaText ? "Universal Retina" : "Universal R";
        // 其余组合直接拼（含 OG + iPad：年代不可能，但按用户要求不特殊处理，交给用户判断）
        return PhoneText(phone) + " + " + PadText(pad, useRetinaText);
    }
}
