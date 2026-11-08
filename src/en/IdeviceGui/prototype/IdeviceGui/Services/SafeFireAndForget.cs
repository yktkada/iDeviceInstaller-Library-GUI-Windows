namespace IdeviceGui.Services;

/// <summary>
/// 无主异步任务的统一收口（2026-11-03 T3）：
/// <para>
/// 代码里所有 `_ = XxxAsync()` / `async void` 事件都必须走这里，理由：
/// ① **异常不许静默消失**——原来 `_ = XxxAsync()` 现场没有任何标记，异常只会进 TaskScheduler.UnobservedTaskException（进程退出才可能触发）；
/// ② 失败要进**状态栏红字 + 日志**（用户 2026-10-30 拍板：异常两处可见）；
/// ③ 操作名写在这里，靠读代码就能知道"谁在后台跑"。
/// </para>
/// <para>
/// WPF 事件处理器的 `async void` 用 <see cref="RunHandler"/> 包一层，把 async void 缩到只剩一个入口；
/// `OperationCanceledException` 视为正常收尾（关窗/暂停取消），不记错。
/// </para>
/// </summary>
internal static class SafeFireAndForget
{
    /// <summary>跑一个无主异步任务；成功静默，失败进日志 + 状态栏（不吞）。</summary>
    internal static Task Run(Func<Task> work, string operation, Action<string, Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(operation);
        return RunCore(work, operation, onError ?? ReportToApp);
    }

    /// <summary>同步委托版本（内部包成已完成的 Task）。</summary>
    internal static Task Run(Action work, string operation, Action<string, Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Run(() =>
        {
            work();
            return Task.CompletedTask;
        }, operation, onError);
    }

    /// <summary>WPF 事件处理器专用：把 `async void` 收敛到一个入口（异常同样不吞）。</summary>
    internal static void RunHandler(Func<Task> work, string operation, Action<string, Exception>? onError = null)
        => _ = Run(work, operation, onError);

    /// <summary>把异常写成"操作名失败：类型: 消息"进日志 + 状态栏（可直接用于已有 try/catch 的事件）。</summary>
    internal static void Report(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(exception);

        var message = $"{operation} did not complete: {exception.GetType().Name}: {exception.Message}";
        try { CrashReporter.Write("FireAndForget." + operation, exception); } catch { /* 崩溃日志失败不能掩盖原异常 */ }
        try { App.ReportUnhandled(new InvalidOperationException(message, exception)); } catch { /* 上报失败同样不能递归 */ }
    }

    private static void ReportToApp(string message, Exception exception)
    {
        try { App.ReportUnhandled(new InvalidOperationException(message, exception)); } catch { /* 上报失败不能递归 */ }
    }

    private static async Task RunCore(Func<Task> work, string operation, Action<string, Exception> onError)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗 / 暂停取消：正常收尾，不记错（与全局异常口径一致）
        }
        catch (Exception exception)
        {
            var message = $"{operation} did not complete: {exception.GetType().Name}: {exception.Message}";
            try { CrashReporter.Write("FireAndForget." + operation, exception); } catch { /* 同上 */ }
            try { onError(message, exception); } catch { /* 上报失败不能递归 */ }
        }
    }
}
