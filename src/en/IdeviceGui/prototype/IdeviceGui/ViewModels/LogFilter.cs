using IdeviceGui.Services;

namespace IdeviceGui.ViewModels;

/// <summary>日志面板显示模式（b3a，2026-10-31 用户拍板）。</summary>
internal enum LogMode
{
    /// <summary>简洁：只显示开始 / 结束 / 错误 / 告警；其余行仍记录但不显示。</summary>
    Simple,

    /// <summary>详细：全部显示。</summary>
    Detail,
}

/// <summary>
/// 日志筛选规则（b3a）。
/// <para>
/// **口径（2026-10-31 复审 P0-1 采纳后改为"Explicitly label class of call points"）**：显示与否只看 <see cref="LogKind"/>，
/// 不再用关键词猜文案——`Start / End / Error` 显示；`Detail` 隐藏（但照常记录）。
/// 唯一保留的文本规则是：`Detail` 行里**工具原始输出**的 `WARNING:` / `ERROR:` 也显示（用户拍板"Both errors and warnings are displayed"），
/// 以及过滤解密 IPA 安装时固定出现的两条无意义告警。
/// </para>
/// </summary>
internal static class LogFilter
{
    public const string SimpleValue = "simple";
    public const string DetailValue = "detail";

    /// <summary>解密 IPA 的两条固定告警（无意义，简洁模式里也不显示）。</summary>
    private static readonly string[] BenignLocateHints =
    {
        "iTunesMetadata.plist",
        ".sinf",
    };

    /// <summary>
    /// 是否是解密 IPA 安装时"could not locate …"那两条固定告警（<c>iTunesMetadata.plist</c> 与 <c>SC_Info/*.sinf</c>）。
    /// 实测原文（2026-10-31，run_fail.log / run_same.log）：
    /// <c>WARNING: could not locate iTunesMetadata.plist in archive!</c>、
    /// <c>WARNING: could not locate Payload/X.app/SC_Info/X.sinf in archive!</c>。
    /// </summary>
    public static bool IsBenignNoise(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (!line.Contains("could not locate", StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var hint in BenignLocateHints)
        {
            if (line.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>界面是否显示这一行：详细模式全显示；简洁模式看类别（并过滤两条固定告警）。</summary>
    public static bool ShouldShow(LogLine line, LogMode mode)
    {
        if (mode == LogMode.Detail) return true;
        if (IsBenignNoise(line.Text)) return false;

        if (line.Kind is LogKind.Start or LogKind.End or LogKind.Error) return true;

        // 工具原始输出里的告警/错误（Detail 类别）：用户 2026-10-31 拍板"Both errors and warnings are displayed"
        return line.Text.Contains("WARNING:", StringComparison.Ordinal)
            || line.Text.Contains("ERROR:", StringComparison.Ordinal);
    }

    /// <summary>Settings 值 → 模式（坏值 / 空 → 简洁，符合"The finished product is not noisy by default"）。</summary>
    public static LogMode ParseMode(string? raw)
    {
        var trimmed = raw?.Trim().Trim('"');
        return string.Equals(trimmed, DetailValue, StringComparison.OrdinalIgnoreCase) ? LogMode.Detail : LogMode.Simple;
    }

    /// <summary>模式 → Settings 值。</summary>
    public static string ToSettingValue(LogMode mode) => mode == LogMode.Detail ? DetailValue : SimpleValue;
}
