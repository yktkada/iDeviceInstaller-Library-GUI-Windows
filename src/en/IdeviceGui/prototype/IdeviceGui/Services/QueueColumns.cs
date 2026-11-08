using System.Text.Json;
using IdeviceGui.ViewModels;

namespace IdeviceGui.Services;

/// <summary>
/// 队列行显示字段（2026-11-08 用户定）：默认只显示 状态·备注·名称·版本·安装后大小，
/// 其余字段可在设置面板勾选并排序；不做 DataGrid，仍拼一个 Display 字符串。
/// </summary>
internal enum QueueColumn
{
    Status,
    Note,
    Name,
    Version,
    InstalledSize,
    BundleId,
    FilePath,
    Origin,
    MinOs,
    DeviceFamily,
}

internal readonly record struct QueueColumnSetting(QueueColumn Column, bool Visible, int Order);

internal static class QueueColumns
{
    public const string SettingsKey = "Queue.Columns";

    public static IReadOnlyList<QueueColumnSetting> Default { get; } = BuildDefault();

    /// <summary>按配置取可见字段，按 order 升序；同 order 按默认顺序稳定。</summary>
    public static IReadOnlyList<QueueColumn> OrderedVisible(IReadOnlyList<QueueColumnSetting> settings)
    {
        var visible = settings.Where(s => s.Visible)
                              .OrderBy(s => s.Order)
                              .ThenBy(s => DefaultOrder(s.Column))
                              .Select(s => s.Column)
                              .ToList();
        // 设置面板允许逐列取消；全部取消时不能把队列行变成空字符串。
        return visible.Count > 0 ? visible : [QueueColumn.Status];
    }

    public static string Format(JobViewModel job, IReadOnlyList<QueueColumn> columns)
    {
        var parts = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            switch (column)
            {
                case QueueColumn.Status:
                    parts.Add($"[{job.StatusText}{job.DisplayPercentText}]");
                    break;
                case QueueColumn.Note:
                    // 失败、暂停、挂起显示原因；完成/取消不显示（2026-11-08 用户拍板 B）。
                    if (job.Status is not (QueueStatus.Failed or QueueStatus.Paused or QueueStatus.Suspended)
                        || string.IsNullOrWhiteSpace(job.Note))
                        continue;
                    parts.Add(job.Note.Trim());
                    break;
                case QueueColumn.Name:
                    parts.Add(job.DisplayNameText);
                    break;
                case QueueColumn.Version:
                    parts.Add(job.PackageVersionText);
                    break;
                case QueueColumn.InstalledSize:
                    parts.Add(job.InstalledSizeText);
                    break;
                case QueueColumn.BundleId:
                    parts.Add(job.BundleId);
                    break;
                case QueueColumn.FilePath:
                    parts.Add(job.FilePathText);
                    break;
                case QueueColumn.Origin:
                    parts.Add(job.OriginText);
                    break;
                case QueueColumn.MinOs:
                    parts.Add(job.MinOsText);
                    break;
                case QueueColumn.DeviceFamily:
                    parts.Add(job.DeviceFamilyText);
                    break;
            }
        }

        return string.Join(" | ", parts);
    }

    public static string Header(QueueColumn column) => column switch
    {
        QueueColumn.Status => "Status",
        QueueColumn.Note => "Remarks",
        QueueColumn.Name => "Name",
        QueueColumn.Version => "Version",
        QueueColumn.InstalledSize => "Size after installation",
        QueueColumn.BundleId => "BundleId",
        QueueColumn.FilePath => "File path",
        QueueColumn.Origin => "Source",
        QueueColumn.MinOs => "Minimum iOS",
        QueueColumn.DeviceFamily => "Device family",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Queue column has no display name defined"),
    };

    public static IReadOnlyList<QueueColumnSetting> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Default; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Default;

            var seen = new HashSet<QueueColumn>();
            var parsed = new List<QueueColumnSetting>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (id is null || !TryParseColumn(id, out var column) || !seen.Add(column)) continue;

                var visible = !item.TryGetProperty("visible", out var visEl)
                              || visEl.ValueKind != JsonValueKind.False;
                var order = item.TryGetProperty("order", out var ordEl) && ordEl.TryGetInt32(out var o)
                    ? o
                    : DefaultOrder(column);
                parsed.Add(new QueueColumnSetting(column, visible, order));
            }

            if (parsed.Count == 0) return Default;
            foreach (var d in Default)
                if (!seen.Contains(d.Column)) parsed.Add(d);
            return parsed;
        }
    }

    public static string Serialize(IReadOnlyList<QueueColumnSetting> settings)
    {
        var payload = settings
            .OrderBy(s => s.Order)
            .ThenBy(s => DefaultOrder(s.Column))
            .Select(s => new Dictionary<string, object>
            {
                ["id"] = ToId(s.Column),
                ["visible"] = s.Visible,
                ["order"] = s.Order,
            });
        return JsonSerializer.Serialize(payload);
    }

    public static string ToId(QueueColumn column) => column switch
    {
        QueueColumn.Status => "status",
        QueueColumn.Note => "note",
        QueueColumn.Name => "name",
        QueueColumn.Version => "version",
        QueueColumn.InstalledSize => "installedSize",
        QueueColumn.BundleId => "bundleId",
        QueueColumn.FilePath => "filePath",
        QueueColumn.Origin => "origin",
        QueueColumn.MinOs => "minOs",
        QueueColumn.DeviceFamily => "deviceFamily",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Queue column has no persistence id defined"),
    };

    public static bool TryParseColumn(string id, out QueueColumn column)
    {
        switch (id.Trim().ToLowerInvariant())
        {
            case "status": column = QueueColumn.Status; return true;
            case "note": column = QueueColumn.Note; return true;
            case "name": column = QueueColumn.Name; return true;
            case "version": column = QueueColumn.Version; return true;
            case "installedsize": column = QueueColumn.InstalledSize; return true;
            case "bundleid": column = QueueColumn.BundleId; return true;
            case "filepath": column = QueueColumn.FilePath; return true;
            case "origin": column = QueueColumn.Origin; return true;
            case "minios": column = QueueColumn.MinOs; return true;
            case "devicefamily": column = QueueColumn.DeviceFamily; return true;
            default: column = QueueColumn.Status; return false;
        }
    }

    private static int DefaultOrder(QueueColumn column)
    {
        for (var i = 0; i < Default.Count; i++)
            if (Default[i].Column == column) return Default[i].Order;
        return int.MaxValue;
    }

    private static QueueColumnSetting[] BuildDefault() =>
    [
        new(QueueColumn.Status, true, 0),
        new(QueueColumn.Note, true, 1),
        new(QueueColumn.Name, true, 2),
        new(QueueColumn.Version, true, 3),
        new(QueueColumn.InstalledSize, true, 4),
        new(QueueColumn.BundleId, false, 5),
        new(QueueColumn.FilePath, false, 6),
        new(QueueColumn.Origin, false, 7),
        new(QueueColumn.MinOs, false, 8),
        new(QueueColumn.DeviceFamily, false, 9),
    ];
}
