using System.Globalization;
using System.Text;

namespace AfcHelper;

/// <summary>
/// AFC 清理 / 空间查询控制台（docs/10 §3.1 协议）：
/// <code>
/// AfcHelper clean --udid &lt;UDID&gt;
/// AfcHelper space --udid &lt;UDID&gt;
/// </code>
/// stdout 每行一个固定 key=value（UTF-8，无 BOM）；stderr 写错误文本；
/// 退出码 0 成功 / 1 本地错误 / 2 参数错误。主程序只解析 stdout 的固定键，不读自由文本。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitLocalError = 1;
    private const int ExitUsage = 2;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static int Main(string[] args)
    {
        // 直接写标准流并显式 UTF-8：不依赖控制台代码页，重定向时也是 UTF-8。
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true };
        using var stderr = new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true };

        try
        {
            return Run(args, stdout, stderr);
        }
        catch (Exception ex)
        {
            // 兜底：native 意外异常也只走 stderr + 退出码，不往 stdout 写自由文本。
            stderr.WriteLine("内部错误：" + ex.Message);
            return ExitLocalError;
        }
    }

    private static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            WriteUsage(stderr);
            return ExitUsage;
        }

        var command = args[0];
        var isClean = string.Equals(command, "clean", StringComparison.OrdinalIgnoreCase);
        var isSpace = string.Equals(command, "space", StringComparison.OrdinalIgnoreCase);
        if (!isClean && !isSpace)
        {
            stderr.WriteLine($"未知命令：{command}");
            WriteUsage(stderr);
            return ExitUsage;
        }

        if (!TryGetUdid(args, out var udid))
        {
            stderr.WriteLine("缺少 --udid <UDID>");
            WriteUsage(stderr);
            return ExitUsage;
        }

        if (!AfcSession.TryOpen(udid, out var session, out var openError))
        {
            // 连不上设备 / lockdown 或 AFC 服务起不来：致命，主程序据此暂停该设备队列。
            stdout.WriteLine("unreachable=1");
            stderr.WriteLine(openError);
            return ExitLocalError;
        }

        using (session)
        {
            return isClean
                ? RunClean(session, stdout)
                : RunSpace(session, stdout, stderr);
        }
    }

    private static int RunClean(AfcSession session, TextWriter stdout)
    {
        var summary = session.CleanStaging();
        foreach (var warning in summary.Warnings)
            stdout.WriteLine("warning=" + OneLine(warning));
        stdout.WriteLine("removed=" + summary.Removed.ToString(CultureInfo.InvariantCulture));

        // 清理遇错不阻断安装（07 §3.2）：即使有 warning 也是退出码 0。
        return ExitOk;
    }

    private static int RunSpace(AfcSession session, TextWriter stdout, TextWriter stderr)
    {
        if (!session.TryReadSpace(out var totalBytes, out var freeBytes, out var error))
        {
            stderr.WriteLine(error);
            return ExitLocalError;
        }

        stdout.WriteLine("total=" + totalBytes.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("free=" + freeBytes.ToString(CultureInfo.InvariantCulture));
        return ExitOk;
    }

    /// <summary>支持 --udid &lt;UDID&gt; 与 --udid=&lt;UDID&gt; 两种写法。</summary>
    private static bool TryGetUdid(string[] args, out string udid)
    {
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--udid=", StringComparison.OrdinalIgnoreCase))
            {
                udid = arg["--udid=".Length..].Trim();
                return udid.Length > 0;
            }

            if (string.Equals(arg, "--udid", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) break;
                udid = args[i + 1].Trim();
                return udid.Length > 0;
            }
        }

        udid = "";
        return false;
    }

    /// <summary>value 里不能带换行，否则会破坏“一行一个键值”的协议。</summary>
    private static string OneLine(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    private static void WriteUsage(TextWriter stderr)
    {
        stderr.WriteLine("用法：AfcHelper clean --udid <UDID>");
        stderr.WriteLine("      AfcHelper space --udid <UDID>");
    }
}
