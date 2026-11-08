using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace IdeviceGui.Services;

/// <summary>一次子进程运行被终止的原因。</summary>
internal enum ProcessStopReason
{
    /// <summary>进程自己退出（正常结束）。</summary>
    Exited,

    /// <summary>按"No output"判卡死，已杀进程树（07 §3.3 的核心判据）。</summary>
    IdleTimeout,

    /// <summary>按总时长判超时，已杀进程树（AFC helper 的"Total duration + no output"用）。</summary>
    TotalTimeout,

    /// <summary>兜底硬超时：整个调用（含杀完等退出）超上限，已尽力杀进程树。</summary>
    HardTimeout,

    /// <summary>调用方取消，已杀进程树（关窗 / 用户暂停用）。</summary>
    Canceled,

    /// <summary>进程没能启动（找不到 exe / 权限不足等）。</summary>
    StartError,
}

/// <summary>
/// 通用子进程执行参数。看门狗语义按 07 §3.3：
/// <list type="bullet">
/// <item><see cref="IdleTimeout"/>：**无输出**超过该时长即判卡死（不是按总时长）。</item>
/// <item><see cref="TotalTimeout"/>：总时长上限，到点即杀（AFC helper 这类"Very little output but 30 s total duration"的调用用）。</item>
/// <item><see cref="HardTimeout"/>：兜底安全阀，涵盖杀进程后等退出的时间；防止"Output continues but never ends"或
/// "Can't kill"的进程把调用方挂住。正常调用不用传。</item>
/// </list>
/// 超时 / 取消都只杀进程树并返回结果，不抛异常。
/// </summary>
internal sealed class ProcessRunOptions
{
    /// <summary>可执行文件完整路径（或 PATH 里的名字）。</summary>
    public required string FileName { get; init; }

    /// <summary>参数逐个给出（ProcessStartInfo.ArgumentList，不拼字符串，避免引号/空格问题）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>工作目录；为空则继承当前进程。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>无输出判据。null = 不按无输出判卡死。</summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>总时长判据。null = 不按总时长判。install / uninstall 必须传 null（07 §3.3）。</summary>
    public TimeSpan? TotalTimeout { get; init; }

    /// <summary>整个调用的兜底硬上限（含杀进程后的等待）。一般不用。</summary>
    public TimeSpan? HardTimeout { get; init; }

    /// <summary>stdout 每行回调（在后台线程上调用，实现方不要碰 UI 线程）。</summary>
    public Action<string>? OnStdOutLine { get; init; }

    /// <summary>stderr 每行回调（在后台线程上调用）。</summary>
    public Action<string>? OnStdErrLine { get; init; }

    /// <summary>看门狗轮询间隔，默认 100 ms。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>读写输出用的编码，默认 UTF-8（工具链输出为 UTF-8 / ASCII）。</summary>
    public Encoding OutputEncoding { get; init; } = Encoding.UTF8;
}

/// <summary>子进程运行结果。超时 / 取消都走这里返回，调用方不接异常。</summary>
internal sealed class ProcessRunResult
{
    public required ProcessStopReason StopReason { get; init; }

    /// <summary>退出码；被超时 / 取消 / 启动失败时按 -1 处理（无意义）。</summary>
    public int ExitCode { get; init; } = -1;

    /// <summary>从启动到返回的耗时（含杀进程后的等待）。</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>超时 / 取消的判据说明，可直接写日志。</summary>
    public string? TimeoutDetail { get; init; }

    /// <summary>启动失败原因（<see cref="ProcessStopReason.StartError"/> 时非 null）。</summary>
    public string? StartError { get; init; }

    /// <summary>输出行回调自身抛出的异常（吞掉，不冒泡到进程事件线程）。</summary>
    public string? CallbackError { get; init; }

    public required IReadOnlyList<string> StdOutLines { get; init; }

    public required IReadOnlyList<string> StdErrLines { get; init; }

    /// <summary>是否被看门狗判超时并杀掉（超时 ≠ 取消）。</summary>
    public bool TimedOut => StopReason is
        ProcessStopReason.IdleTimeout or ProcessStopReason.TotalTimeout or ProcessStopReason.HardTimeout;

    public bool Canceled => StopReason == ProcessStopReason.Canceled;

    public bool Started => StopReason != ProcessStopReason.StartError;

    public bool Succeeded => StopReason == ProcessStopReason.Exited && ExitCode == 0;
}

/// <summary>
/// 子进程执行器：异步 + 可取消 + 按输出活动判卡死 + 超时杀进程树（07 §3.3、总览硬约束 #2 / #3）。
/// <para>
/// 判卡死的口径是"**How long has it been since there was no new output**"，不是"How long did you run in total?"——2 GB 的包上传 10 分钟是正常的，
/// 只要 <c>Transfer:</c> 还在走就不能杀。所以 install / uninstall 只传 IdleTimeout，不传 TotalTimeout。
/// </para>
/// <para>
/// 超时与取消都会 <c>Kill(entireProcessTree: true)</c> 后返回 <see cref="ProcessRunResult"/>，不抛异常；
/// 调用方负责把"timeout"翻译成业务结果、把"Cancel"翻译成 <see cref="OperationCanceledException"/>。
/// <see cref="KillAll"/> 是关窗兜底：保证主程序退出时没有残留的 ideviceinstaller / AfcHelper。
/// </para>
/// <para>线程安全：可在任意线程并发调用；每个进程独立看门狗，不设全局锁（硬约束 #3）。</para>
/// </summary>
internal static class ProcessRunner
{
    /// <summary>活动进程注册表：kill 后从 finally 移除。键是 PID（仅在进程存活期间使用）。</summary>
    private static readonly ConcurrentDictionary<int, Process> Running = new();

    /// <summary>请求杀进程后，最多等它退出的宽限（留出排空输出 / 进程树逐层退出的时间）。</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(3);

    /// <summary>当前仍在跑的托管子进程数（诊断 / 关窗检查用）。</summary>
    public static int ActiveProcessCount => Running.Count;

    /// <summary>
    /// 关窗兜底：把当前所有托管子进程按进程树杀掉。返回发起杀进程的次数（不代表全部杀成功）。
    /// 正常关窗顺序是"Cancel CancellationToken → await each device pump → KillAll"，本方法只补最后一道。
    /// </summary>
    public static int KillAll()
    {
        var count = 0;
        foreach (var pair in Running)
        {
            if (TryKillTree(pair.Value))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>启动并等待一个子进程，按 <paramref name="options"/> 判超时。不抛异常（启动失败也返回结果）。</summary>
    public static async Task<ProcessRunResult> RunAsync(ProcessRunOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var clock = Stopwatch.StartNew();
        var stdout = new List<string>();
        var stderr = new List<string>();
        // 输出事件在 ThreadPool 线程上触发：行列表与"Last activity time"都要加锁 / 原子读写
        var bufferGate = new object();
        var lastActivityTicks = Stopwatch.GetTimestamp();
        string? callbackError = null;

        if (ct.IsCancellationRequested)
        {
            return new ProcessRunResult
            {
                StopReason = ProcessStopReason.Canceled,
                Elapsed = clock.Elapsed,
                TimeoutDetail = "Cancelled before start",
                StdOutLines = Array.Empty<string>(),
                StdErrLines = Array.Empty<string>(),
            };
        }

        var psi = new ProcessStartInfo(options.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = options.OutputEncoding,
            StandardErrorEncoding = options.OutputEncoding,
        };
        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            psi.WorkingDirectory = options.WorkingDirectory;
        }

        foreach (var argument in options.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // EnableRaisingEvents 必须在 Start() 之前设好，否则极短命的进程可能在订阅 Exited 之前就退出（漏事件 → 看门狗白等）
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void InvokeCallback(Action<string>? callback, string line)
        {
            if (callback is null)
            {
                return;
            }

            try
            {
                callback(line);
            }
            catch (Exception ex)
            {
                // 回调（日志 / 进度上报）失败不能打断进程读取，更不能让异常逃出事件线程
                Interlocked.CompareExchange(ref callbackError, ex.GetType().Name + ": " + ex.Message, null);
            }
        }

        DataReceivedEventHandler onStdout = (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            Interlocked.Exchange(ref lastActivityTicks, Stopwatch.GetTimestamp());
            lock (bufferGate)
            {
                stdout.Add(e.Data);
            }

            InvokeCallback(options.OnStdOutLine, e.Data);
        };

        DataReceivedEventHandler onStderr = (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            Interlocked.Exchange(ref lastActivityTicks, Stopwatch.GetTimestamp());
            lock (bufferGate)
            {
                stderr.Add(e.Data);
            }

            InvokeCallback(options.OnStdErrLine, e.Data);
        };

        EventHandler onExited = (_, _) => exitTcs.TrySetResult(true);

        process.OutputDataReceived += onStdout;
        process.ErrorDataReceived += onStderr;
        process.Exited += onExited;

        var started = false;
        var exited = false;
        var exitCode = -1;
        var stopReason = ProcessStopReason.Exited;
        string? timeoutDetail = null;

        try
        {
            try
            {
                process.Start();
                started = true;
            }
            catch (Exception ex) when (IsStartFailure(ex))
            {
                return new ProcessRunResult
                {
                    StopReason = ProcessStopReason.StartError,
                    Elapsed = clock.Elapsed,
                    StartError = ex.Message,
                    StdOutLines = Array.Empty<string>(),
                    StdErrLines = Array.Empty<string>(),
                };
            }

            Running[process.Id] = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // ── 看门狗：等进程退出，同时每 PollInterval 检查一次"No output duration / total duration / hard cap / cancel" ──
            var hardDeadline = options.HardTimeout is { } hardLimit ? clock.Elapsed + hardLimit : (TimeSpan?)null;
            while (true)
            {
                var tick = Task.Delay(options.PollInterval, CancellationToken.None);
                var completed = await Task.WhenAny(exitTcs.Task, tick).ConfigureAwait(false);
                if (completed == exitTcs.Task)
                {
                    break;   // 进程自己退了
                }

                if (ct.IsCancellationRequested)
                {
                    stopReason = ProcessStopReason.Canceled;
                    timeoutDetail = $"Cancelled by caller (process terminated). Ran for {clock.Elapsed.TotalSeconds:F1} s";
                    break;
                }

                var idle = Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivityTicks));
                if (options.IdleTimeout is { } idleLimit && idle >= idleLimit)
                {
                    stopReason = ProcessStopReason.IdleTimeout;
                    timeoutDetail = $"No output for {idle.TotalSeconds:F1} s (threshold {idleLimit.TotalSeconds:F1} s); process terminated";
                    break;
                }

                if (options.TotalTimeout is { } totalLimit && clock.Elapsed >= totalLimit)
                {
                    stopReason = ProcessStopReason.TotalTimeout;
                    timeoutDetail = $"Total runtime {clock.Elapsed.TotalSeconds:F1} s (limit {totalLimit.TotalSeconds:F1} s); process terminated";
                    break;
                }

                if (hardDeadline is { } deadline && clock.Elapsed >= deadline)
                {
                    stopReason = ProcessStopReason.HardTimeout;
                    timeoutDetail = $"Hard timeout of {options.HardTimeout!.Value.TotalSeconds:F1} s reached; process terminated";
                    break;
                }
            }

            if (stopReason != ProcessStopReason.Exited)
            {
                TryKillTree(process);

                // 杀完最多再等 KillGrace；HardTimeout 有剩就按剩余时间等，绝不无限等
                var grace = KillGrace;
                if (hardDeadline is { } deadline)
                {
                    var remaining = deadline - clock.Elapsed;
                    grace = remaining <= TimeSpan.Zero ? TimeSpan.Zero : (remaining < grace ? remaining : grace);
                }

                if (grace > TimeSpan.Zero)
                {
                    await Task.WhenAny(exitTcs.Task, Task.Delay(grace, CancellationToken.None)).ConfigureAwait(false);
                }
            }

            exited = SafeHasExited(process);
            if (exited)
            {
                // 进程已退出时 WaitForExit()（无参）立即返回，作用是等异步输出事件把管道里剩余行排空；
                // 不在这里读输出会丢掉最后几行（install 的 Installing / ERROR 往往就在末尾）。
                try
                {
                    process.WaitForExit();
                }
                catch (InvalidOperationException)
                {
                    // 进程已在排空时被释放 / 状态异常：保持已有缓冲
                }

                try
                {
                    exitCode = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    exitCode = -1;
                }
            }
        }
        finally
        {
            // 成对释放：先退订事件，再注销注册表（using 负责 Dispose 句柄）
            process.OutputDataReceived -= onStdout;
            process.ErrorDataReceived -= onStderr;
            process.Exited -= onExited;
            if (started)
            {
                Running.TryRemove(process.Id, out _);
            }
        }

        List<string> stdoutSnapshot;
        List<string> stderrSnapshot;
        lock (bufferGate)
        {
            stdoutSnapshot = new List<string>(stdout);
            stderrSnapshot = new List<string>(stderr);
        }

        return new ProcessRunResult
        {
            StopReason = stopReason,
            ExitCode = exited ? exitCode : -1,
            Elapsed = clock.Elapsed,
            TimeoutDetail = timeoutDetail,
            CallbackError = callbackError,
            StdOutLines = stdoutSnapshot,
            StdErrLines = stderrSnapshot,
        };
    }

    /// <summary>杀进程树；进程已退出 / 已释放都不抛（超时路径必须有幂等的失败处理）。</summary>
    private static bool TryKillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;   // 还没 Start / 已退出 / 句柄已释放（ObjectDisposedException 也归这类）
        }
        catch (Win32Exception)
        {
            return false;   // 权限不足 / 进程刚好消失
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsStartFailure(Exception ex) => ex is
        Win32Exception or
        FileNotFoundException or
        InvalidOperationException or
        ArgumentException or
        NotSupportedException;
}
