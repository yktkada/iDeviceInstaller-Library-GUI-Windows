using System.Windows.Threading;

namespace IdeviceGui.ViewModels;

/// <summary>
/// 只执行一次的 UI 定时器（可取消）。b3b：日志 50 ms 批量上屏、搜索输入防抖共用。
/// 抽成接口是为了单测能注入"手动触发"的实现，不去等真实 Dispatcher 时钟。
/// </summary>
internal interface IUiTimer
{
    /// <summary>delay 后执行一次 action；返回的对象 Dispose 即取消（未执行则不再执行）。</summary>
    IDisposable ScheduleOnce(TimeSpan delay, Action action);
}

/// <summary>生产实现：走 WPF Dispatcher（UI 线程回调，不需要 Dispatcher.Invoke）。</summary>
internal sealed class DispatcherUiTimer : IUiTimer
{
    public IDisposable ScheduleOnce(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new Cancel(timer);
    }

    private sealed class Cancel : IDisposable
    {
        private readonly DispatcherTimer _timer;
        public Cancel(DispatcherTimer timer) => _timer = timer;
        public void Dispose() => _timer.Stop();
    }
}
