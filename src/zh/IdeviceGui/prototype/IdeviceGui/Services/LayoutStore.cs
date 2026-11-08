using System.Globalization;
using System.Text;
using IdeviceGui.Services.Data;

namespace IdeviceGui.Services;

/// <summary>
/// 三栏布局的持久化状态（docs/01 §2、§2.1）：比例、日志栏是否隐藏、最大化浮层宽度。
/// <para>
/// 只存**比例**不存像素（窗口缩放 / 高 DPI 下不错位）；浮层宽度是浮层自身的宽度，按 DIP 存。
/// </para>
/// </summary>
internal sealed record LayoutState(double[] Columns, bool LogHidden, double LogOverlayWidth)
{
    /// <summary>默认 45 / 27 / 28，日志栏显示，浮层 420 DIP（docs/01 §2 / §2.1）。</summary>
    public static LayoutState Default { get; } = new(new[] { 45d, 27d, 28d }, false, 420d);
}

/// <summary>
/// 布局状态的读写（`Settings` 表三行：`Layout.MainColumns` / `Layout.LogHidden` / `Layout.LogOverlayWidth`）。
/// 坏值 / 缺省 / 库不可用一律回默认——界面先能起来，比严格校验重要（docs/09 §3.2）。
/// </summary>
internal static class LayoutStore
{
    public const string ColumnsKey = "Layout.MainColumns";
    public const string LogHiddenKey = "Layout.LogHidden";
    public const string LogOverlayWidthKey = "Layout.LogOverlayWidth";

    /// <summary>三栏最小宽度（DIP，docs/01 §2）。</summary>
    public const double MinLeftWidth = 360d;
    public const double MinMiddleWidth = 260d;
    public const double MinRightWidth = 240d;

    /// <summary>分隔条宽度（DIP）。</summary>
    public const double SplitterWidth = 5d;

    public const double DefaultOverlayWidth = 420d;
    public const double MinOverlayWidth = 120d;
    public const double MaxOverlayWidth = 1200d;

    /// <summary>读布局；db 为空（库没打开）或读失败都返回默认值。</summary>
    public static LayoutState Load(AppDatabase? db)
    {
        if (db is null) return LayoutState.Default;
        try
        {
            var columns = ParseColumns(SettingsStore.GetString(db, ColumnsKey));
            var hidden = SettingsStore.GetBool(db, LogHiddenKey, fallback: false);
            var overlay = ParseWidth(SettingsStore.GetString(db, LogOverlayWidthKey), DefaultOverlayWidth);
            return new LayoutState(columns, hidden, overlay);
        }
        catch
        {
            return LayoutState.Default;
        }
    }

    /// <summary>写布局；一次事务写三行（关窗时只抢一次写锁）。失败只记文件日志，不影响关窗。</summary>
    public static void Save(AppDatabase? db, LayoutState state, Action<string>? log = null)
    {
        if (db is null) return;
        var columns = FormatColumns(state.Columns);
        var hidden = state.LogHidden ? "true" : "false";
        var overlay = FormatWidth(state.LogOverlayWidth);

        try
        {
            // T7：不再自写一份 Settings upsert SQL（三份 → 一份，见 SettingsStore.Upsert）
            SettingsStore.SetMany(db, new[]
            {
                new KeyValuePair<string, string>(ColumnsKey, columns),
                new KeyValuePair<string, string>(LogHiddenKey, hidden),
                new KeyValuePair<string, string>(LogOverlayWidthKey, overlay),
            });
        }
        catch (Exception ex)
        {
            // 布局存不下来不影响使用（下次按默认比例开）；写不了文件日志就算了
            try { log?.Invoke($"未能保存界面布局：{ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    /// <summary>解析比例：接受 `[45,27,28]` / `45,27,28` / `45 27 28`；坏值回默认。</summary>
    internal static double[] ParseColumns(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('[', ']', '{', '}');
        if (text.Length == 0) return LayoutState.Default.Columns;

        var parts = text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return LayoutState.Default.Columns;

        var values = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return LayoutState.Default.Columns;
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return LayoutState.Default.Columns;
            values[i] = value;
        }

        return Normalize(values);
    }

    /// <summary>按比例归一化到总和 100（保留两位小数），保证存进库的值稳定可读。</summary>
    internal static double[] Normalize(IReadOnlyList<double> columns)
    {
        var sum = 0d;
        foreach (var value in columns) sum += value;
        if (sum <= 0 || double.IsNaN(sum) || double.IsInfinity(sum)) return LayoutState.Default.Columns;

        var result = new double[3];
        for (var i = 0; i < 3; i++) result[i] = Math.Round(columns[i] / sum * 100d, 2, MidpointRounding.AwayFromZero);
        return result;
    }

    /// <summary>写成 JSON 数组（docs/09 §3.2 的值统一字符串口径）。</summary>
    internal static string FormatColumns(IReadOnlyList<double> columns)
    {
        var normalized = Normalize(columns);
        var builder = new StringBuilder("[");
        for (var i = 0; i < normalized.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(normalized[i].ToString("0.##", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>解析浮层宽度：坏值回默认，越界夹到 [120, 1200]。</summary>
    internal static double ParseWidth(string? raw, double fallback)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return fallback;
        if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
        return Math.Clamp(value, MinOverlayWidth, MaxOverlayWidth);
    }

    internal static string FormatWidth(double width) =>
        Math.Clamp(width, MinOverlayWidth, MaxOverlayWidth).ToString("0.##", CultureInfo.InvariantCulture);
}
