namespace IdeviceGui.ViewModels;

/// <summary>多选行契约：资料库行和设备应用行共用拖动范围选择。</summary>
internal interface IMultiSelectRow
{
    bool IsMultiSelected { get; set; }
}

/// <summary>
/// 拖动范围选择的唯一实现：从 anchor 到 current 的行相对拖动前状态切换（Explorer 风格）。
/// 资料库与设备应用页都调这里，不在两个页面各写一套。
/// </summary>
internal static class MultiSelectRange
{
    public static bool Apply<T>(
        IReadOnlyList<T> rows,
        T anchor,
        T current,
        IReadOnlySet<T> baseSelection,
        Func<T, bool> skip) where T : class, IMultiSelectRow
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], anchor)) first = i;
            if (ReferenceEquals(rows[i], current)) last = i;
        }
        if (first < 0 || last < 0) return false;
        if (first > last) (first, last) = (last, first);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (skip(row)) continue;
            var inRange = i >= first && i <= last;
            row.IsMultiSelected = baseSelection.Contains(row) ^ inRange;
        }
        return true;
    }
}
