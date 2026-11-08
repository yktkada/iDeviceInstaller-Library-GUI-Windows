using System.IO;

namespace IdeviceGui.Services;

/// <summary>
/// 启动参数（M3 临时用途，M4 会正式做"多扫描根"UI）。
/// <para>
/// 目前只支持一个参数：<c>--root &lt;目录&gt;</c>（也接受 <c>--root=&lt;目录&gt;</c>）。
/// 不给就用程序目录下的 library。未知参数一律忽略——
/// 这是自用程序，宁可少认一个参数，也不要因为多打一个开关起不来。
/// </para>
/// </summary>
internal sealed record StartupOptions(string? ScanRoot)
{
    public static StartupOptions Parse(string[]? args)
    {
        if (args is null || args.Length == 0) return new StartupOptions((string?)null);

        string? root = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is null) continue;

            if (arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
            {
                root = Normalize(arg["--root=".Length..]);
                continue;
            }

            if (string.Equals(arg, "--root", StringComparison.OrdinalIgnoreCase))
            {
                // 后面必须跟一个值；没有值就当没给（不要吞掉下一个开关）
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    root = Normalize(args[i + 1]);
                    i++;
                }
            }
        }

        return new StartupOptions(root);
    }

    private static string? Normalize(string value)
    {
        var trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0) return null;
        try
        {
            // 统一成绝对路径（相对路径按当前工作目录解析）；解析失败就保留原文，交给扫描阶段报错
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }
}
