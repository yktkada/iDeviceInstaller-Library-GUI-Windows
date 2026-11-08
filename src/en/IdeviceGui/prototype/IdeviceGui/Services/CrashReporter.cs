using System.IO;
using System.Text;

namespace IdeviceGui.Services;

/// <summary>
/// 崩溃日志写入器。路径与语义见 docs/08 §3.2：
/// <b>写 <c>&lt;程序目录&gt;\logs\crash.log</c>，覆盖写、只留最后一次</b>；同样只写程序目录。
/// <para>
/// 独立成类是为了能脱离 WPF 进程验证（B6 之后可单测）：路径解析在 <see cref="AppPaths"/>，
/// 这里只负责"把最后一条异常落盘，且落盘失败绝不抛"。
/// </para>
/// </summary>
internal static class CrashReporter
{
    public static string Path => AppPaths.CrashLogPath;

    /// <summary>把一条崩溃写进 crash.log（覆盖上一次）。任何 IO 异常都被吞掉——记录崩溃时不能再抛。</summary>
    public static void Write(string source, Exception? exception)
    {
        try
        {
            var text = BuildText(source, exception);

            Directory.CreateDirectory(AppPaths.LogsDirectory);

            // 覆盖写：只留最后一次（08 §3.2）。写临时文件再替换，避免写一半被再次崩溃打断后留下残file。
            var target = AppPaths.CrashLogPath;
            var temp = target + ".tmp";
            File.WriteAllText(temp, text, Encoding.UTF8);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            // 记录崩溃时不能再抛异常
        }
    }

    internal static string BuildText(string source, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
            .Append(source)
            .Append(" · pid=").Append(Environment.ProcessId)
            .Append(" · .NET ").Append(Environment.Version)
            .Append(" · ").Append(Environment.OSVersion)
            .AppendLine();
        builder.AppendLine(exception?.ToString() ?? "(no exception object)");
        builder.AppendLine(new string('-', 60));
        return builder.ToString();
    }
}
