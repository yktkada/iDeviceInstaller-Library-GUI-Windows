using System.Text;

namespace IdeviceGui.Services;

/// <summary>
/// 日志导出（2026-11-01 用户要求）：把**全量**内存缓冲（最多 2000 行）导成 txt。
/// <para>格式：文件头 3 行 + 分隔线 + 每行 `HH:mm:ss.fff [Kind] [设备(型号)] 全文`；
/// 界面是简洁模式时也照样导出全量（排障需要过程行），区别只由 `[Kind]` 体现。</para>
/// </summary>
internal static class LogExport
{
    public const string FileHeader = "iDeviceInstaller+Library 日志导出";

    /// <summary>默认文件名：`日志-yyyyMMdd-HHmmss.txt`（默认目录由调用方给程序目录）。</summary>
    public static string DefaultFileName(DateTime now) => $"日志-{now:yyyyMMdd-HHmmss}.txt";

    /// <summary>生成导出文本（UTF-8 带 BOM 由写盘侧负责）。</summary>
    public static string Build(IReadOnlyList<LogLine> lines, DateTime exportedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FileHeader);
        sb.AppendLine("导出时间：" + exportedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine($"行数：{lines.Count} / 2000");
        sb.AppendLine(new string('-', 60));

        foreach (var line in lines)
        {
            sb.Append(line.Time.ToString("HH:mm:ss.fff")).Append(' ').AppendLine(line.FormatForFile());
        }

        return sb.ToString();
    }
}
