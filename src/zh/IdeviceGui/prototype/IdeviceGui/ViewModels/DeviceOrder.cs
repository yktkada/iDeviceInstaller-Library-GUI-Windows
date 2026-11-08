using System.Collections.ObjectModel;

namespace IdeviceGui.ViewModels;

/// <summary>
/// 设备 tab 排序（2026-11-02 用户定）：**在线的排前面、离线的排最后**，各自保持原有相对顺序（稳定）。
/// 纯逻辑，便于单测。
/// </summary>
internal static class DeviceOrder
{
    /// <summary>返回重排后的索引序列（online 先、offline 后，组内稳定）。</summary>
    public static int[] Compute<T>(IReadOnlyList<T> items, Func<T, bool> isOnline)
    {
        var order = new int[items.Count];
        var next = 0;
        for (var i = 0; i < items.Count; i++)
            if (isOnline(items[i])) order[next++] = i;
        for (var i = 0; i < items.Count; i++)
            if (!isOnline(items[i])) order[next++] = i;
        return order;
    }

    /// <summary>原地重排集合（ObservableCollection.Move 会发 CollectionChanged，界面跟着动）。</summary>
    public static void Arrange<T>(ObservableCollection<T> items, Func<T, bool> isOnline)
    {
        var order = Compute(items, isOnline);
        for (var target = 0; target < order.Length; target++)
        {
            var current = items.IndexOf(items[order[target]]);
            if (current != target) items.Move(current, target);
        }
    }

    public static void Arrange<T>(ObservableCollection<T> items) where T : class =>
        Arrange(items, item => item switch
        {
            DeviceQueueViewModel queue => queue.Device.Online,
            _ => true,
        });
}
