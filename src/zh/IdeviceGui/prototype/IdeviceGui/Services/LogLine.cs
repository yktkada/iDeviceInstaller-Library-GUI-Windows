using System.Threading;

namespace IdeviceGui.Services;

/// <summary>日志行类别（b3a 口径，2026-10-31 复审 P0-1 采纳后由"关键词匹配"改为"调用点显式标注"）。</summary>
internal enum LogKind
{
    /// <summary>过程行：进度、回读、空间检查细节等。简洁模式不显示（但照常记录）。</summary>
    Detail,

    /// <summary>分界行：任务开始。</summary>
    Start,

    /// <summary>分界行：任务结束（完成 / 已暂停 / 已取消 / 需确认 / 入队结果）。</summary>
    End,

    /// <summary>错误行。简洁模式也要显示（用户拍板）。</summary>
    Error,
}

/// <summary>
/// 一条日志（b3b 结构化，2026-11-01）。**约定**（复审 P1-A / P2-H / P3-I 采纳）：
/// <list type="number">
/// <item><see cref="Text"/> 存**全文**，截断只发生在渲染层（`TextTrimming` + ToolTip）；</item>
/// <item>时间在**创建**这一行时抓（`Seq` 自增保证唯一，避免 `ScrollIntoView` 用相等性定位到第一条）；</item>
/// <item>界面与文件格式只由 <see cref="FormatForUi"/>/<see cref="FormatForFile"/> 产出，单一来源；</item>
/// <item><see cref="ToString"/> 返回界面文本——UIA 名称靠它（`AGENTS §6` 第 3 条）。</item>
/// </list>
/// </summary>
internal sealed class LogLine
{
    private static long _seq;

    public LogLine(
        string text,
        LogKind kind = LogKind.Detail,
        DateTime? time = null,
        string? udid = null,
        string? deviceName = null,
        string? productType = null)
    {
        Seq = Interlocked.Increment(ref _seq);
        Text = text ?? "";
        Kind = kind;
        Time = time ?? DateTime.Now;
        Udid = string.IsNullOrWhiteSpace(udid) ? null : udid;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        ProductType = string.IsNullOrWhiteSpace(productType) ? null : productType;
    }

    /// <summary>自增序号：行唯一标识（值相等不影响定位/ScrollIntoView）。</summary>
    public long Seq { get; }

    /// <summary>正文全文（**不截断**；过滤规则也依赖它）。</summary>
    public string Text { get; }

    public LogKind Kind { get; }

    /// <summary>产生时刻（界面显示 HH:mm:ss，文件日志带毫秒）。</summary>
    public DateTime Time { get; }

    public string? Udid { get; }

    /// <summary>设备显示名（全局行为 null）。</summary>
    public string? DeviceName { get; }

    /// <summary>真实机型（ProductType，如 iPhone5,2；用于识别伪装机型）。</summary>
    public string? ProductType { get; }

    /// <summary>XAML 触发器用：错误行整行红色（不依赖 internal enum 的字符串转换，复审 P1-C）。</summary>
    public bool IsError => Kind == LogKind.Error;

    /// <summary>XAML 触发器用：结束行整行绿色。</summary>
    public bool IsEnd => Kind == LogKind.End;

    public static LogLine Detail(string text, string? udid = null, string? deviceName = null, string? productType = null)
        => new(text, LogKind.Detail, udid: udid, deviceName: deviceName, productType: productType);

    public static LogLine Start(string text, string? udid = null, string? deviceName = null, string? productType = null)
        => new(text, LogKind.Start, udid: udid, deviceName: deviceName, productType: productType);

    public static LogLine End(string text, string? udid = null, string? deviceName = null, string? productType = null)
        => new(text, LogKind.End, udid: udid, deviceName: deviceName, productType: productType);

    public static LogLine Error(string text, string? udid = null, string? deviceName = null, string? productType = null)
        => new(text, LogKind.Error, udid: udid, deviceName: deviceName, productType: productType);

    /// <summary>界面格式：`[Error] 13:10:26 [iPhone 5(iPhone5,2)] 内容`；全局行不带设备段。</summary>
    public string FormatForUi(bool truncateDeviceName = true) => Format("HH:mm:ss", truncateDeviceName);

    /// <summary>
    /// 文件日志格式：`[Kind] [设备名(型号)] 全文`（设备名不截断）。
    /// **不含时间**：<see cref="FileLog"/> 写在行首（`HH:mm:ss.fff`），这里再加会双重时间戳。
    /// </summary>
    public string FormatForFile() => Format("", truncateDeviceName: false);

    /// <summary>ToolTip 用：UI 文本但**不截断设备名**。</summary>
    public string ToolTipText => FormatForUi(truncateDeviceName: false);

    private string Format(string timeFormat, bool truncateDeviceName)
    {
        var stamp = timeFormat.Length == 0 ? "" : " " + Time.ToString(timeFormat);
        var head = $"[{Kind}]{stamp}";

        if (DeviceName is null && ProductType is null) return head + " " + Text;

        var name = DeviceName ?? "未知设备";
        if (truncateDeviceName && name.Length > 12) name = name[..12] + "…";
        var model = ProductType is null ? "" : "(" + ProductType + ")";
        return $"{head} [{name}{model}] {Text}";
    }

    public override string ToString() => FormatForUi();
}

/// <summary>
/// 把 <c>IProgress&lt;string&gt;</c>（AFC helper 等既有通道）桥到本项目的 <see cref="LogLine"/>，
/// 统一按 Detail 记录（这些通道本来就是过程日志）。
/// </summary>
internal sealed class StringLogBridge(Action<string> detail) : IProgress<string>
{
    public void Report(string value) => detail(value);
}
