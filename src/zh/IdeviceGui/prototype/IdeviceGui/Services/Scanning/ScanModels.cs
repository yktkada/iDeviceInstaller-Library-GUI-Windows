namespace IdeviceGui.Services.Scanning;

/// <summary>扫描开关。全部来自 docs，不暴露到 UI。</summary>
internal sealed class ScanOptions
{

    /// <summary>解析阶段并发度。docs/03 §3.4 定为 2。</summary>
    public int ParseConcurrency { get; set; } = 2;

    /// <summary>解析阶段每攒够多少行就落库一次（M4-a；2026-11-08 用户拍板默认 1000，减少 SQLite 事务次数）。</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>
    /// 全量重扫（2026-10-31 用户要的按钮）：解析阶段**连 `Ok` 行也重新解析**，
    /// 用来补"旧版本入库、深字段（体积 / 图标 / 架构）为空"的行。默认 false = 只解析 Pending / Failed。
    /// </summary>
    public bool ForceReparse { get; set; }

    public const int DefaultBatchSize = 1000;

    /// <summary>设置键：分批大小。</summary>
    public const string BatchSizeKey = "Scan.BatchSize";

    /// <summary>批次大小上限：防止设置被写成 2147483647 时直接 OOM（审核报告 P2-2）。</summary>
    public const int MaxBatchSize = 1000;

    /// <summary>把设置里的文本解析成批次大小；空 / 非数字 / ≤0 一律回退默认，超上限夹到 <see cref="MaxBatchSize"/>。</summary>
    public static int ResolveBatchSize(string? raw, int fallback = DefaultBatchSize) =>
        int.TryParse(raw?.Trim().Trim('"'), out var value) && value > 0
            ? Math.Min(value, MaxBatchSize)
            : fallback;
}

/// <summary>扫描进度（枚举 / 解析两个阶段）。</summary>
internal sealed record ScanProgress(string Phase, int Done, int Total);

/// <summary>一次扫描的原始计数与耗时——只报数字，不判通过（`规则.md` R2）。</summary>
internal sealed class ScanResult
{
    public bool RootMissing { get; set; }
    public string? Error { get; set; }

    public int TotalFiles { get; set; }
    public int Added { get; set; }
    public int Moved { get; set; }
    public int Replaced { get; set; }
    public int Touched { get; set; }
    public int Missing { get; set; }
    public int IdentityFlips { get; set; }
    public int Unchanged { get; set; }

    public int Parsed { get; set; }

    /// <summary>解析阶段的落库成功次数（M4-a 分批提交，报数据用）。</summary>
    public int SaveCount { get; set; }

    /// <summary>落库失败批次数（失败批次整批回滚、行留 Pending，下次扫描会重试）。</summary>
    public int SaveFailed { get; set; }

    /// <summary>因为"落库连续失败"而放弃尝试的批次数（库长期不可写时提前收手，审核 P2-2）。</summary>
    public int SaveAborted { get; set; }
    public int ParseOk { get; set; }
    public int ParseFailed { get; set; }

    public long ListPhaseMs { get; set; }
    public long ParsePhaseMs { get; set; }
    public long TotalMs { get; set; }

    /// <summary>
    /// 本轮扫描是否**可能改变库内容**（2026-11-04 审查 P1）：新增/移动/替换/仅大小时间/缺失/身份翻转，
    /// 以及任何解析结果（成功或失败——失败会把深字段清空，也必须同步到界面）。
    /// 新增计数器时**必须**加到这里，别在调用方手抄。
    /// </summary>
    public bool HasLibraryChange =>
        Added > 0 || Moved > 0 || Replaced > 0 || Touched > 0 ||
        Missing > 0 || IdentityFlips > 0 || ParseOk > 0 || ParseFailed > 0;

    /// <summary>
    /// 一行摘要。**扫描被跳过时（<see cref="Error"/> 非空）必须以"扫描跳过"开头**：
    /// 否则库里不可写会显示成"扫描完成：文件 0"（审核 P1-1）。
    /// </summary>
    public string Summary => Error is { Length: > 0 }
        ? "跳过扫描：" + Error
        : $"扫描完成：文件 {TotalFiles}；新增 {Added}、移动 {Moved}、替换 {Replaced}、仅更新大小/时间 {Touched}、" +
          $"标为不存在 {Missing}、身份翻转 {IdentityFlips}、未变 {Unchanged}；" +
          $"解析 {Parsed}（成功 {ParseOk} / 失败 {ParseFailed}）；" +
          $"分批落库 {SaveCount} 次" + (SaveFailed > 0 ? $"（失败 {SaveFailed} 批）" : "") +
          (SaveAborted > 0 ? $"（放弃 {SaveAborted} 批）" : "") +
          $"；枚举 {ListPhaseMs} ms、解析 {ParsePhaseMs} ms、合计 {TotalMs} ms";
}
