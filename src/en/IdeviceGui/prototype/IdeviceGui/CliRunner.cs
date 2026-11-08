using System.Globalization;
using System.IO;
using System.Text;
using IdeviceGui.Services;
using IdeviceGui.Services.Diagnostics;

namespace IdeviceGui;

/// <summary>
/// ideviceinstaller / idevice_id / ideviceinfo 的异步封装（实现 ICliRunner）。
/// <para>
/// 所有子进程都走 <see cref="ProcessRunner"/>：按"No output"判卡死 + 超时 / 取消杀进程树（07 §3.3）。
/// 本类没有 UI 亲和性：方法全 async、不阻塞调用线程；日志与进度只通过 IProgress 上报，
/// UI 线程由调用方（QueueService / ViewModel）用 Task.Run / await 保证不阻塞（硬约束 #2）。
/// </para>
/// <para>
/// 取消语义：ProcessRunner 杀完进程树返回 Canceled，本类再按 .NET 惯例抛
/// <see cref="OperationCanceledException"/>——QueueService 靠它区分"User pauses/closes window"与"Installation failed"。
/// 超时语义：一律返回 TimedOut 结果（InstallOutcome / UninstallOutcome / DeviceProbe.Missing /
/// DiskUsage.Unknown），不抛异常。
/// </para>
/// </summary>
internal sealed class CliRunner : ICliRunner
{
    /// <summary>idevice_id -l 超时（07 §3.3 超时表：5 s）。</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);

    /// <summary>ideviceinfo 探测 / 容量查询超时（07 §3.3：15 s）。</summary>
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// install / uninstall 的判据：**180 s 无新输出**才判卡死，不是总时长（07 §3.3）。
    /// 2 GB 的包上传十分钟是正常的，只要 Transfer 还在走就不能杀。
    /// </summary>
    private static readonly TimeSpan OperationIdleTimeout = TimeSpan.FromSeconds(180);

    /// <summary>回读 `list --xml` 超时（07 §3.3：30 s；超时 = 结果未知，标"Needs confirmation"）。</summary>
    private static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(30);

    public string ToolkitDir { get; }

    public CliRunner()
    {
        ToolkitDir = ResolveToolkitDir();
    }

    public string Tool(string name) => Path.Combine(ToolkitDir, name);

    // ── ICliRunner ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListUdidsAsync(CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(new ProcessRunOptions
        {
            FileName = Tool("idevice_id.exe"),
            Arguments = new[] { "-l" },
            WorkingDirectory = ToolkitDir,
            IdleTimeout = ListTimeout,
            TotalTimeout = ListTimeout,
        }, ct).ConfigureAwait(false);

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
        }

        // 超时 / 启动失败 → 视为没有设备在线（07 §3.3：超时视为设备离线）
        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            return Array.Empty<string>();
        }

        var udids = new List<string>();
        foreach (var line in result.StdOutLines)
        {
            var udid = line.Trim();
            if (udid.Length > 0 && !udids.Contains(udid, StringComparer.Ordinal))
            {
                udids.Add(udid);
            }
        }

        return udids;
    }

    /// <inheritdoc />
    public async Task<DeviceProbe> ProbeAsync(string udid, CancellationToken ct)
    {
        // 硬约束 #1：先确认设备在 idevice_id -l 里，再读具体字段
        var udids = await ListUdidsAsync(ct).ConfigureAwait(false);
        if (!udids.Contains(udid, StringComparer.Ordinal))
        {
            return DeviceProbe.Missing;
        }

        var result = await ProcessRunner.RunAsync(new ProcessRunOptions
        {
            FileName = Tool("ideviceinfo.exe"),
            Arguments = new[] { "-u", udid },
            WorkingDirectory = ToolkitDir,
            IdleTimeout = InfoTimeout,
            TotalTimeout = InfoTimeout,
        }, ct).ConfigureAwait(false);

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            return DeviceProbe.Missing;   // 15 s 超时 = 设备无响应（07 §3.3）
        }

        return new DeviceProbe(
            Present: true,
            Name: FindValue(result.StdOutLines, "DeviceName"),
            ProductType: FindValue(result.StdOutLines, "ProductType"),
            Version: FindValue(result.StdOutLines, "ProductVersion"),
            CpuArch: FindValue(result.StdOutLines, "CPUArchitecture"),
            Build: FindValue(result.StdOutLines, "BuildVersion"),
            Color: FindValue(result.StdOutLines, "DeviceColor"),
            Ecid: FindValue(result.StdOutLines, "UniqueChipID"),
            Serial: FindValue(result.StdOutLines, "SerialNumber"),
            ModelNumber: FindValue(result.StdOutLines, "ModelNumber"),
            HardwareModel: FindValue(result.StdOutLines, "HardwareModel"));
    }

    /// <inheritdoc />
    public async Task<InstallOutcome> InstallAsync(
        string udid,
        string ipaPath,
        IProgress<string>? log,
        IProgress<double>? percent,
        CancellationToken ct)
    {
        var errorLines = new List<string>();
        percent?.Report(0);

        ProcessRunResult result;
        try
        {
            result = await ProcessRunner.RunAsync(new ProcessRunOptions
            {
                FileName = Tool("ideviceinstaller.exe"),
                Arguments = new[] { "-u", udid, "install", ipaPath },
                WorkingDirectory = ToolkitDir,

                // 只按"No new output"判卡死，不设总时长（07 §3.3）
                IdleTimeout = OperationIdleTimeout,

                OnStdOutLine = line =>
                {
                    log?.Report(line);
                    if (percent is not null && TryParseTransferPercent(line, out var value))
                    {
                        percent.Report(value);
                    }
                },
                OnStdErrLine = line =>
                {
                    // 成功判定只用 stderr 的 ERROR 行；WARNING（未签名包缺 SC_Info）是正常输出，照常显示不判失败
                    if (IsErrorLine(line))
                    {
                        lock (errorLines)
                        {
                            errorLines.Add(line);
                        }

                        log?.Report("[err] " + line);
                    }
                    else
                    {
                        log?.Report(line);
                    }
                },
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ProcessRunner 本身不抛超时异常；这里只兜真正意外的故障（如磁盘/句柄问题）
            log?.Report("Install process error: " + ex.Message);
            return new InstallOutcome(-1, false, null, false);
        }

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (result.TimedOut)
        {
            log?.Report(result.TimeoutDetail ?? "Install timed out; install process terminated");
            return new InstallOutcome(-1, false, null, true);
        }

        if (!result.Started)
        {
            log?.Report("Failed to start install process: " + result.StartError);
            return new InstallOutcome(-1, false, null, false);
        }

        string? firstError;
        lock (errorLines)
        {
            firstError = errorLines.FirstOrDefault();
        }

        return new InstallOutcome(result.ExitCode, firstError is not null, firstError, false);
    }

    /// <inheritdoc />
    public async Task<UninstallOutcome> UninstallAsync(
        string udid,
        string bundleId,
        IProgress<string>? log,
        CancellationToken ct)
    {
        var errorLines = new List<string>();

        ProcessRunResult result;
        try
        {
            result = await ProcessRunner.RunAsync(new ProcessRunOptions
            {
                FileName = Tool("ideviceinstaller.exe"),
                Arguments = new[] { "-u", udid, "uninstall", bundleId },
                WorkingDirectory = ToolkitDir,
                IdleTimeout = OperationIdleTimeout,
                OnStdOutLine = line => log?.Report(line),
                OnStdErrLine = line =>
                {
                    if (IsErrorLine(line))
                    {
                        lock (errorLines)
                        {
                            errorLines.Add(line);
                        }

                        log?.Report("[err] " + line);
                    }
                    else
                    {
                        log?.Report(line);
                    }
                },
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Report("Uninstall process error: " + ex.Message);
            return new UninstallOutcome(-1, false, null, false);
        }

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (result.TimedOut)
        {
            log?.Report(result.TimeoutDetail ?? "Uninstall timed out; uninstall process terminated");
            return new UninstallOutcome(-1, false, null, true);
        }

        if (!result.Started)
        {
            log?.Report("Failed to start uninstall process: " + result.StartError);
            return new UninstallOutcome(-1, false, null, false);
        }

        string? firstError;
        lock (errorLines)
        {
            firstError = errorLines.FirstOrDefault();
        }

        return new UninstallOutcome(result.ExitCode, firstError is not null, firstError, false);
    }

    /// <inheritdoc />
    public async Task<DiskUsage> QueryDiskUsageAsync(string udid, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(new ProcessRunOptions
        {
            FileName = Tool("ideviceinfo.exe"),
            Arguments = new[] { "-u", udid, "-q", "com.apple.disk_usage" },
            WorkingDirectory = ToolkitDir,
            IdleTimeout = InfoTimeout,
            TotalTimeout = InfoTimeout,
        }, ct).ConfigureAwait(false);

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            return DiskUsage.Unknown;
        }

        // 实测键名（总览 §6.9）：TotalDataCapacity / TotalDataAvailable，单位是字节。
        // 输出里的 NANDInfo 是超长 base64，必须按键名精确前缀匹配，不能模糊包含匹配。
        var total = FindLongValue(result.StdOutLines, "TotalDataCapacity");
        var free = FindLongValue(result.StdOutLines, "TotalDataAvailable");

        if (total is null || total <= 0 || free is null || free < 0)
        {
            return DiskUsage.Unknown;
        }

        return new DiskUsage(true, total.Value, free.Value);
    }

    /// <inheritdoc />
    public async Task<BatteryUsage> ReadBatteryUsageAsync(string udid, CancellationToken ct)
    {
        var text = await RunInfoCommandAsync(
            new[] { "-u", udid, "-q", "com.apple.mobile.battery" }, ct).ConfigureAwait(false);
        return BatteryInfoReader.ParseUsage(text);
    }

    /// <inheritdoc />
    public async Task<BatteryHealth> ReadBatteryHealthAsync(string udid, CancellationToken ct)
    {
        var text = await RunInfoCommandAsync(
            new[] { "-u", udid, "diagnostics", "GasGauge" }, ct, "idevicediagnostics.exe").ConfigureAwait(false);
        return BatteryInfoReader.ParseGasGauge(text);
    }

    /// <inheritdoc />
    public async Task<BatterySpec> ReadBatterySpecAsync(string udid, CancellationToken ct)
    {
        var text = await RunInfoCommandAsync(
            new[] { "-u", udid, "ioregentry", "AppleARMPMUCharger" }, ct, "idevicediagnostics.exe").ConfigureAwait(false);
        return BatteryInfoReader.ParseChargerEntry(text);
    }

    /// <summary>跑一次只读 ideviceinfo / idevicediagnostics，返回 stdout 文本（失败给 null，由解析方回退 Unknown）。</summary>
    private async Task<string?> RunInfoCommandAsync(string[] args, CancellationToken ct, string exe = "ideviceinfo.exe")
    {
        var result = await ProcessRunner.RunAsync(new ProcessRunOptions
        {
            FileName = Tool(exe),
            Arguments = args,
            WorkingDirectory = ToolkitDir,
            IdleTimeout = InfoTimeout,
            TotalTimeout = InfoTimeout,
            OutputEncoding = Encoding.UTF8,
        }, ct).ConfigureAwait(false);

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (!result.Started || result.TimedOut)
        {
            return null;
        }

        // GasGauge / ioregentry 在旧 iOS 上会以非 0 退出（无该节点）：stdout 可能是错误文本，交给解析器判空
        return string.Join("\n", result.StdOutLines);
    }

    /// <inheritdoc />
    public async Task<string?> ListInstalledXmlAsync(string udid, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(new ProcessRunOptions
        {
            FileName = Tool("ideviceinstaller.exe"),

            // 回读是只读短命令：30 s 无输出 / 总时长 30 s（07 §3.3）
            Arguments = new[] { "-u", udid, "list", "--xml" },
            WorkingDirectory = ToolkitDir,
            IdleTimeout = ReadbackTimeout,
            TotalTimeout = ReadbackTimeout,

            // XML 里可能有非 ASCII 的 App 名，必须按 UTF-8 读（工具链输出为 UTF-8）
            OutputEncoding = Encoding.UTF8,
        }, ct).ConfigureAwait(false);

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
        }

        // 超时 / 启动失败 / 退出码非 0 → 结果未知，由 ReadbackService 标"Needs confirmation"（07 §3.3）
        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        // 只做进程侧：返回原始 XML 文本，解析留在 ReadbackService（可脱离进程单测）
        return string.Join("\n", result.StdOutLines);
    }

    // ── 解析（纯函数，便于回归） ──────────────────────────────────────────────

    /// <summary>
    /// 从 ideviceinstaller 输出行解析**安装段**进度：`Install: CreatingStagingDirectory (5%)` → 5（0..100）。
    /// 2026-11-03 用户给出真机样本（11 个阶段：5/15/20/20/30/40/50/60/70/80/90）——传输段只有 `Transfer: x%`，
    /// 安装段只有这种行，所以两段分别解析、分别显示。
    /// </summary>
    internal static bool TryParseInstallPercent(string line, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith("Install:", StringComparison.Ordinal)) return false;

        var close = trimmed.LastIndexOf(')');
        if (close < 0) return false;
        var open = trimmed.LastIndexOf('(', close);
        if (open < 0 || close <= open + 1) return false;

        var inner = trimmed[(open + 1)..close].Trim();
        if (!inner.EndsWith("%", StringComparison.Ordinal)) return false;
        if (!double.TryParse(inner[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return false;

        percent = Math.Clamp(value, 0, 100);
        return true;
    }

    /// <summary>从 ideviceinstaller 输出行解析 <c>Transfer: x%</c> 的百分比（0..100）。</summary>
    internal static bool TryParseTransferPercent(string line, out double percent)
    {
        percent = 0;
        const string marker = "Transfer:";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var cursor = index + marker.Length;
        while (cursor < line.Length && (line[cursor] == ' ' || line[cursor] == '\t'))
        {
            cursor++;
        }

        var start = cursor;
        while (cursor < line.Length && (char.IsAsciiDigit(line[cursor]) || line[cursor] == '.'))
        {
            cursor++;
        }

        if (cursor == start)
        {
            return false;
        }

        var number = line[start..cursor];
        while (cursor < line.Length && (line[cursor] == ' ' || line[cursor] == '\t'))
        {
            cursor++;
        }

        if (cursor >= line.Length || line[cursor] != '%')
        {
            return false;
        }

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (value is < 0 or > 100)
        {
            return false;
        }

        percent = value;
        return true;
    }

    private static bool IsErrorLine(string line) =>
        line.StartsWith("ERROR", StringComparison.Ordinal);

    /// <summary>读 <c>Key: value</c> 形式的输出（ideviceinfo）。</summary>
    private static string FindValue(IReadOnlyList<string> lines, string key)
    {
        var prefix = key + ":";
        foreach (var line in lines)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return "";
    }

    /// <summary>读 <c>Key: long</c> 形式的输出（disk_usage）。</summary>
    private static long? FindLongValue(IReadOnlyList<string> lines, string key)
    {
        var text = FindValue(lines, key);
        if (text.Length == 0)
        {
            return null;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    // ── 工具链定位 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 查找顺序（10 §3.1）：程序目录 → 程序目录\toolkit。
    /// 最后一项是开发机兜底，**只在 DEBUG 生效**（Release 发布版不认开发机绝对路径）。
    /// </summary>
    private static string ResolveToolkitDir()
    {
        var candidates = new List<string>
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "toolkit"),
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "ideviceinstaller.exe")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find ideviceinstaller.exe. Searched: " + string.Join(" | ", candidates));
    }
}
