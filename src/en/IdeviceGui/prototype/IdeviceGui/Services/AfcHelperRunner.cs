using System.Globalization;
using System.IO;
using System.Text;

namespace IdeviceGui.Services;

/// <summary>
/// <see cref="IAfcHelper"/> 的进程实现：启动 <c>程序目录\AfcHelper.exe</c>（docs/10 §3.1 协议），
/// 按 30 s 总时长 + 无输出判卡死，超时 <c>Kill(entireProcessTree: true)</c>（07 §3.3、总览 §6.4）。
/// 主程序不直接 P/Invoke AFC；stdout 只解析固定 key=value，不解析自由文本。
/// </summary>
internal sealed class AfcHelperRunner : IAfcHelper
{
    private const string HelperFileName = "AfcHelper.exe";

    // 协议里约定的固定键（docs/10 §3.1）。只认这些键，其余行一律忽略。
    private const string KeyRemoved = "removed";
    private const string KeyWarning = "warning";
    private const string KeyUnreachable = "unreachable";
    private const string KeyTotal = "total";
    private const string KeyFree = "free";

    /// <summary>AFC helper 超时判据：30 s 总时长（07 §3.3）。</summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>兜底硬上限：30 s 总时长 + 杀树与排空输出的余量（体检报告 P0-4）。</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(45);

    /// <summary>helper 输出很少，连续这么久没有任何输出就判卡死，不必等满 30 s。</summary>
    private static readonly TimeSpan NoOutputTimeout = TimeSpan.FromSeconds(15);

    private const string TimeoutWarning = "AfcHelper terminated: timed out";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _helperPath;

    /// <summary>默认在同目录找 AfcHelper.exe；测试可显式指定路径。</summary>
    public AfcHelperRunner(string? helperPath = null) =>
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, HelperFileName);

    public async Task<AfcCleanOutcome> CleanStagingAsync(string udid, IProgress<string>? log, CancellationToken ct)
    {
        if (!File.Exists(_helperPath))
        {
            log?.Report($"Could not find AfcHelper: {_helperPath}");
            return MissingHelperCleanOutcome();
        }

        HelperRun run;
        try
        {
            run = await RunAsync("clean", udid, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 本地错误（如 helper 被占用 / 无执行权限）：清理不阻断安装，只记警告。
            log?.Report("Failed to start AfcHelper: " + ex.Message);
            return new AfcCleanOutcome(0, DeviceUnreachable: false, TimedOut: false, Warning: "Failed to start AfcHelper: " + ex.Message);
        }

        return ToCleanOutcome(run);
    }

    public async Task<AfcSpaceOutcome> QuerySpaceAsync(string udid, CancellationToken ct)
    {
        if (!File.Exists(_helperPath))
            return new AfcSpaceOutcome(DeviceUnreachable: false, TimedOut: false, TotalBytes: 0, FreeBytes: 0,
                Error: $"Could not find AfcHelper.exe ({_helperPath})");

        HelperRun run;
        try
        {
            run = await RunAsync("space", udid, log: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AfcSpaceOutcome(false, false, 0, 0, "Failed to start AfcHelper: " + ex.Message);
        }

        return ToSpaceOutcome(run);
    }

    private static AfcCleanOutcome ToCleanOutcome(HelperRun run)
    {
        if (run.TimedOut)
            return new AfcCleanOutcome(0, DeviceUnreachable: false, TimedOut: true, Warning: TimeoutWarning);

        var parsed = ParseStdout(run.Stdout);
        if (parsed.Unreachable)
            return new AfcCleanOutcome(0, DeviceUnreachable: true, TimedOut: false, Warning: null);

        var warnings = new List<string>(parsed.Warnings);
        if (run.ExitCode != 0)
        {
            var first = FirstLine(run.Stderr);
            warnings.Add(first is null ? $"AfcHelper exit code {run.ExitCode}" : $"AfcHelper exit code {run.ExitCode}: {first}");
        }
        else if (parsed.Removed is null)
        {
            warnings.Add("AfcHelper did not return removed");
        }

        return new AfcCleanOutcome(
            parsed.Removed ?? 0,
            DeviceUnreachable: false,
            TimedOut: false,
            Warning: warnings.Count == 0 ? null : string.Join("；", warnings));
    }

    private static AfcSpaceOutcome ToSpaceOutcome(HelperRun run)
    {
        if (run.TimedOut)
            return new AfcSpaceOutcome(DeviceUnreachable: false, TimedOut: true, TotalBytes: 0, FreeBytes: 0, Error: TimeoutWarning);

        var parsed = ParseStdout(run.Stdout);
        if (parsed.Unreachable)
            return new AfcSpaceOutcome(DeviceUnreachable: true, TimedOut: false, TotalBytes: 0, FreeBytes: 0, Error: null);

        if (run.ExitCode != 0)
        {
            var error = FirstLine(run.Stderr) ?? $"AfcHelper exit code {run.ExitCode}";
            return new AfcSpaceOutcome(false, false, 0, 0, error);
        }

        if (parsed.Total is not { } total || parsed.Free is not { } free)
            return new AfcSpaceOutcome(false, false, 0, 0, "AfcHelper output is missing total/free");

        return new AfcSpaceOutcome(DeviceUnreachable: false, TimedOut: false, TotalBytes: total, FreeBytes: free, Error: null);
    }

    private static AfcCleanOutcome MissingHelperCleanOutcome() =>
        // 本地错误，不是设备问题：不暂停队列，清理照 07 §3.2 允许失败继续。
        new(0, DeviceUnreachable: false, TimedOut: false, Warning: "AfcHelper.exe not found; cleanup skipped");

    /// <summary>启动 helper 并等它结束。超时/取消都会杀进程树；超时以结果标志返回，取消以异常返回。</summary>
    private async Task<HelperRun> RunAsync(string command, string udid, IProgress<string>? log, CancellationToken ct)
    {
        // 复用 ProcessRunner（体检报告 P0-4）：进程登记进它的活动表 → 关窗 KillAll 能兜底杀到；
        // 超时 / 取消统一按"杀进程树 + 有限宽限等退出 + 硬上限"收尾，不再自己管流与看门狗。
        var result = await ProcessRunner.RunAsync(new ProcessRunOptions
        {
            FileName = _helperPath,
            Arguments = new[] { command, "--udid", udid },
            WorkingDirectory = Path.GetDirectoryName(_helperPath) ?? AppContext.BaseDirectory,
            IdleTimeout = NoOutputTimeout,
            TotalTimeout = TotalTimeout,
            HardTimeout = HardTimeout,
            OnStdOutLine = line => log?.Report(line),
            OnStdErrLine = line => log?.Report(line),
            OutputEncoding = Utf8NoBom,
        }, ct).ConfigureAwait(false);

        if (result.Canceled) throw new OperationCanceledException(ct);

        if (!result.Started)
        {
            var reason = result.StartError is null ? "" : "：" + result.StartError;
            return new HelperRun(TimedOut: false, ExitCode: -1, Stdout: Array.Empty<string>(), Stderr: "Failed to start AfcHelper process" + reason);
        }

        return new HelperRun(
            TimedOut: result.TimedOut,
            ExitCode: result.ExitCode,
            Stdout: result.StdOutLines,
            Stderr: string.Join(Environment.NewLine, result.StdErrLines).Trim());
    }

    /// <summary>只解析 stdout 的固定 key=value 行；没有 '=' 的行直接忽略（绝不解析自由文本）。</summary>
    private static ParsedStdout ParseStdout(IReadOnlyList<string> lines)
    {
        var parsed = new ParsedStdout();
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case KeyRemoved:
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var removed))
                        parsed.Removed = removed;
                    break;

                case KeyWarning:
                    if (value.Length > 0) parsed.Warnings.Add(value);
                    break;

                case KeyUnreachable:
                    if (value == "1") parsed.Unreachable = true;
                    break;

                case KeyTotal:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                        parsed.Total = total;
                    break;

                case KeyFree:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var free))
                        parsed.Free = free;
                    break;

                default:
                    break;
            }
        }

        return parsed;
    }

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var reader = new StringReader(text);
        return reader.ReadLine()?.Trim();
    }

    private readonly record struct HelperRun(bool TimedOut, int ExitCode, IReadOnlyList<string> Stdout, string Stderr);

    /// <summary>stdout 解析结果。键缺失时保持 null，调用方据此区分“0”和“没给”。</summary>
    private sealed class ParsedStdout
    {
        public int? Removed { get; set; }

        public bool Unreachable { get; set; }

        public long? Total { get; set; }

        public long? Free { get; set; }

        public List<string> Warnings { get; } = new();
    }
}
