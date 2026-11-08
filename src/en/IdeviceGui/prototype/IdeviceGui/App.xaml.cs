using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using IdeviceGui.Services;

namespace IdeviceGui;

public partial class App : Application
{
    /// <summary>单实例互斥体名。用会话内名字（不加 Global\），避免需要额外权限。</summary>
    private const string SingleInstanceMutexName = "IdeviceGui.SingleInstance.v1";

    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalExceptionHandlers();

        // ⓪.5 审查 B4（2026-11-03 修）：**先看有没有实例在跑，再决定要不要提权**。
        //     旧顺序是"先 UAC 再查单实例"——第二次双击会先弹一次 UAC，然后才提示"已有实例"。
        //     带 `--elevated` 的是提权后的子进程：父进程这时还没建 Mutex，跳过这个探测直接去抢。
        if (!HasElevatedMarker(e.Args) && IsAnotherInstanceRunning())
        {
            MessageBox.Show(
                "iDeviceInstaller+Library is already running." + Environment.NewLine + Environment.NewLine +
                "Only one instance of iDeviceInstaller+Library is allowed.",
                "Only one instance of iDeviceInstaller+Library is allowed.", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        // ⓪ 启动时请求管理员权限（2026-11-02 用户定）：
        //    本机在**非管理员**令牌下调用 explorer/shell（「打开文件路径」）会被系统拒绝
        //    （explorer.exe 0xC0000142 / "Windows 无法访问指定设备、路径或文件"，踩坑 C32/C33）；
        //    用户实测**右键管理员运行正常** → 启动时主动请求 UAC。
        //    用户拒绝 UAC（Win32Exception 1223）→ **回退普通权限继续运行**（打开路径可能失败，状态栏提示）。
        //    `IDEVICEGUI_SKIP_ELEVATION=1` 供自动化测试跳过（避免弹 UAC 卡住）；`--elevated` 防重复提权。
        if (ShouldRequestElevation(e.Args) && TryRelaunchElevated(e.Args))
        {
            Shutdown(0);
            return;
        }

        // ① 单实例：两个进程同时写库 / 抢 USB 设备都会出事（docs/09 §4、10 §5）。
        //    Mutex 必须先于任何磁盘动作，否则第二个实例会先去建目录、写日志。
        if (!TryAcquireSingleInstance())
        {
            MessageBox.Show(
                "iDeviceInstaller+Library is already running." + Environment.NewLine + Environment.NewLine +
                "Only one instance of iDeviceInstaller+Library is allowed.",
                "Only one instance of iDeviceInstaller+Library is allowed.", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        // ② 程序目录必须可写（硬约束 #7：只写程序目录，严禁用户目录；不可写就提示、不回退）
        if (!AppPaths.EnsureWritable(out var problem))
        {
            MessageBox.Show(problem, "Cannot write to program folder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // ②.5 SQLite 临时文件必须落在程序目录（硬约束 #7；系统 TEMP 在严格沙箱下不可写会 SQLITE_CANTOPEN，踩坑 C27）
        if (!AppPaths.UseProgramTempDirectory(out var tempProblem))
        {
            MessageBox.Show(tempProblem, "Cannot write to temp folder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // ③ 自检通过才建主窗口（App.xaml 已去掉 StartupUri）。
        //    **绝不能靠全局异常兜底处理启动失败**：窗口构造失败后如果只是 throw 让
        //    DispatcherUnhandledException 吞掉，进程会变成"无窗口的 Dispatcher"一直活着，
        //    还占着单实例 Mutex，把后续正常启动全挡在门外（复核 D0，高危）。
        //    所以这里自己 try/catch → 提示 → 显式 Shutdown(1)，让 OnExit 释放 Mutex。
        ICliRunner cli;
        IAfcHelper afc;
        try
        {
            cli = new CliRunner();
            afc = new AfcHelperRunner();
        }
        catch (Exception ex)
        {
            CrashReporter.Write("App.Startup.Toolkit", ex);
            MessageBox.Show(
                ex.Message + Environment.NewLine + Environment.NewLine + "The program will now exit.",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            // 启动参数（M3 临时用 --root 指定扫描根；M4 会有正式的多根 UI）
            var options = StartupOptions.Parse(e.Args);
            var window = new Views.MainWindow(cli, afc, options.ScanRoot);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            CrashReporter.Write("App.Startup.Window", ex);
            MessageBox.Show(
                "Failed to create the main window: " + ex.Message + Environment.NewLine + Environment.NewLine + "The program will now exit.",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool HasElevatedMarker(string[] args)
        => args.Any(a => string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 只探测、不持有：会话内是否已有实例（审查 B4：单实例检查要放在 UAC 提权之前）。
    /// 打不开（存在但无权限）也按"有"处理——宁可少开一个窗口，也不冒双击写坏库的风险。
    /// </summary>
    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(SingleInstanceMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Exception ex)
        {
            CrashReporter.Write("App.Startup.SingleInstanceProbe", ex);
            return false;
        }
    }

    /// <summary>是否还需要请求提权：已管理员 / 自动化跳过 / 本次已带 --elevated 标记 → 不提。</summary>
    private static bool ShouldRequestElevation(string[] args)
    {
        if (Environment.GetEnvironmentVariable("IDEVICEGUI_SKIP_ELEVATION") == "1") return false;

        // 审查 B4(d)（2026-11-03）：旧 L3 脚本很多没设环境变量，逐个改不现实；
        // 支持在数据目录放一个 `.skip-elevation` 文件来全局跳过（开发/自动化一次性设置）。
        try
        {
            if (File.Exists(Path.Combine(AppPaths.DataDirectory, ".skip-elevation"))) return false;
        }
        catch (Exception)
        {
            // 路径异常不影响提权判断
        }

        if (args.Any(a => string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase))) return false;
        return !IsElevated();
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>以管理员重新启动自己；用户拒绝 UAC 或启动失败返回 false（调用方回退普通权限继续）。</summary>
    private static bool TryRelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = BuildRelaunchArguments(args),
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 上点了「否」→ 回退普通权限
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildRelaunchArguments(string[] args)
    {
        var list = new List<string> { "--elevated" };
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--elevated", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(QuoteArg(arg));
        }

        return string.Join(' ', list);
    }

    /// <summary>
    /// Windows 命令行引号（审查 B4）：**尾随反斜杠要成对翻倍**，否则 `--root "C:\path with spaces\"` 会把结尾引号吃掉。
    /// 规则（CommandLineToArgvW）：引号前的 N 个反斜杠 → 2N 个；字符串结尾的 N 个反斜杠 → 2N 个再加引号。
    /// </summary>
    private static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;

        var sb = new StringBuilder(arg.Length + 8);
        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(ch);
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (createdNew) return true;

            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }
        catch (Exception ex)
        {
            // 拿不到 Mutex 不该挡住程序使用；记进崩溃日志并继续（宁可可能双开，也不要打不开）
            CrashReporter.Write("App.Startup.SingleInstance", ex);
            return true;
        }
    }

    private void ReleaseSingleInstance()
    {
        var mutex = _singleInstance;
        _singleInstance = null;
        if (mutex is null) return;

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不是持有者（例如已异常释放）：忽略
        }
        finally
        {
            mutex.Dispose();
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashReporter.Write("AppDomain", args.ExceptionObject as Exception);
            if (args.ExceptionObject is Exception appDomainEx) ReportUnhandled(appDomainEx);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.Write("UnobservedTask", args.Exception);
            ReportUnhandled(args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// 全局未处理异常（UI 线程 / 非 UI 线程）向上报的事件：主窗口据此在状态栏打红字 + 写日志。
    /// 用户 2026-10-30 拍板：异常必须"状态红字 + 日志"两处可见，不能只写 crash.log。
    /// </summary>
    public static event Action<string>? UnhandledErrorReported;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReporter.Write("Dispatcher", e.Exception);
        ReportUnhandled(e.Exception);
        e.Handled = true;   // 不让单个 UI 异常直接杀掉整个程序
    }

    internal static void ReportUnhandled(Exception ex)
    {
        try
        {
            UnhandledErrorReported?.Invoke(ex.GetType().Name + ": " + ex.Message);
        }
        catch
        {
            // 状态栏上报失败不能再抛（否则递归）
        }
    }
}
