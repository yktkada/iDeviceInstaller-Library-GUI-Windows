using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Imaging;
using IdeviceGui.Services.Parsing;

namespace IdeviceGui.Services.Scanning;

/// <summary>枚举到的一个候选文件（列表阶段的原始事实）。</summary>
internal sealed record ScanFile(
    string Path,
    string NormalizedPath,
    string FileName,
    FileIdentity? Identity,
    long Size,
    string MtimeUtc)
{
    public IdentityKind Kind => Identity is null ? IdentityKind.PathFallback : IdentityKind.FileId;
}

/// <summary>一批落库的结局（用于重试逻辑的单测与调用方分支）。</summary>
internal enum SaveAttemptStatus
{
    Saved,
    Failed,
    Cancelled,
}

/// <summary>带重试落库的结果：结局 + 实际尝试次数 + 最后一次异常。</summary>
internal readonly record struct SaveAttempt(SaveAttemptStatus Status, int Attempts, Exception? Error);

/// <summary>
/// M2 扫描器：枚举 → 身份 → 对账 → 解析（docs/03 §3.3 / §3.4）。
/// <para>
/// 两个阶段：**列表/对账**（写库一次事务）→ **解析**（并发 2，逐包独立失败）。
/// 所有方法同步跑在调用方线程上（调用方在后台线程调用，硬约束 #2）。
/// </para>
/// </summary>
internal sealed class IpaScanner
{
    private readonly AppDatabase _db;
    private readonly ScanOptions _options;
    private readonly Action<string>? _log;

    // M3 深解析：图标缓存目录 / 档位与选择参数 / JPEG 兜底解码器（WPF，MTA 下也可能为 null）
    private readonly string? _dataDirectory;
    private readonly IconCacheOptions? _iconOptions;
    private readonly IconRequest _iconRequest;
    private readonly Func<byte[], PngCodec.DecodedPng?>? _jpegDecoder;

    // M4-a：批次回调（已落库的行）+ 落库次数
    private readonly Action<IReadOnlyList<PackageRow>>? _onRowsParsed;
    private int _saveCount;
    private int _saveFailed;

    // 落库重试 / 熔断（审核 P2-1、P2-2）：重试可取消、只对瞬时错误重试、连续失败就停手。
    private const int SaveRetryAttempts = 3;
    private const int SaveRetryDelayMs = 800;
    private const long SaveRetryBudgetMs = 60_000;             // 一轮扫描的重试总预算
    private const int SaveAbortAfterConsecutiveFailures = 2;   // 连续这么多批写不进去就放弃本轮
    private long _saveRetryDeadline;
    private int _consecutiveSaveFailures;
    private int _saveAborted;                                  // 1 = 本轮剩余批次不再尝试
    private int _saveAbortedBatches;

    /// <summary>写探测自带的等锁上限（不用 15 s 的主超时，库被占住时要尽快报"Not writable"）。</summary>
    private const int ProbeBusyTimeoutMs = 2000;

    // M3 图标解码统计（第 2 条风险：WIC 在后台线程上的稳定性要靠真实数据判断，先把数据采出来）
    private readonly ConcurrentDictionary<string, int> _iconErrors = new(StringComparer.Ordinal);
    private int _iconNewFiles;
    private int _iconCacheHits;
    private int _iconFallback;
    private int _iconNoCandidate;

    public IpaScanner(
        AppDatabase db,
        ScanOptions? options = null,
        Action<string>? log = null,
        string? dataDirectory = null,
        IconCacheOptions? iconOptions = null,
        IconRequest? iconRequest = null,
        Func<byte[], PngCodec.DecodedPng?>? jpegDecoder = null,
        Action<IReadOnlyList<PackageRow>>? onRowsParsed = null)
    {
        _db = db;
        _options = options ?? new ScanOptions();
        _log = log;
        _dataDirectory = dataDirectory;
        _iconOptions = iconOptions;
        _iconRequest = iconRequest ?? new IconRequest(32, 1.0);
        _jpegDecoder = jpegDecoder;
        _onRowsParsed = onRowsParsed;
    }

    /// <param name="forceReparse">
    /// **按次**的全量重扫开关（2026-10-31 从共享可变属性改成参数，见 `进度 §5.2` 第 3 条）：
    /// 解析阶段连 `Ok` 行也重解析。放在参数上就不会出现"The state left by the previous call affects the next time"。
    /// </param>
    public async Task<ScanResult> ScanAsync(
        long scanRootId,
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        bool forceReparse = false)
    {
        // 同一库同时只跑一轮扫描（见 AppDatabase.ScanGate 注释）
        await _db.ScanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(scanRootId, rootPath, progress, ct, forceReparse).ConfigureAwait(false);
        }
        finally
        {
            _db.ScanGate.Release();
        }
    }

    private async Task<ScanResult> ScanCoreAsync(
        long scanRootId,
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken ct,
        bool forceReparse)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult();

        if (!Directory.Exists(rootPath))
        {
            // 根目录不可访问：跳过本次对账，**不要**把这批记录标成不存在（docs/03 §3.1）
            result.RootMissing = true;
            result.Error = "Scan root not accessible; skipping this reconcile: " + rootPath;
            _log?.Invoke(result.Error);
            result.TotalMs = sw.ElapsedMilliseconds;
            return result;
        }

        // 写探测（用户 2026-10-30 拍板）：库不可写就别白扫（冷扫 423 包要几十秒）。
        // 只开一个 IMMEDIATE 事务写一行再回滚，几毫秒；抢锁不跨整个扫描。
        var probeError = TryWriteProbe();
        if (probeError is not null)
        {
            result.Error = "Failed to write to the database; skipping this scan: " + probeError;
            _log?.Invoke(result.Error);
            result.TotalMs = sw.ElapsedMilliseconds;
            return result;
        }

        var files = await Task.Run(() => Enumerate(rootPath, progress, ct), ct).ConfigureAwait(false);
        result.TotalFiles = files.Count;

        var rows = PackageStore.LoadByRoot(_db, scanRootId);
        var changes = Reconcile(scanRootId, files, rows, UtcText.Now(), result);
        PackageStore.Save(_db, changes);
        result.ListPhaseMs = sw.ElapsedMilliseconds;

        var parseSw = Stopwatch.StartNew();
        // 除了新行（Pending），每次扫描也重试失败行：不再靠内容哈希判断变化，
        // 而是"Now in the directory, parse it again."，失败的行下次扫描自动再试（用户 2026-10-30 拍板）。
        // **全量重扫**（2026-10-31 用户要的按钮）：连 Ok 行也重解析——
        // 用来补旧版本入库、深字段（体积 / 图标 / 架构）为空的行；ParseVersion 照常 +1。
        // 口径来自本次调用参数（不是共享可变属性，见 进度 §5.2 第 3 条）。
        var pending = PackageStore.LoadByRoot(_db, scanRootId)
            .Where(r => r.ExistsFlag && (forceReparse || r.ParseState is ParseStates.Pending or ParseStates.Failed))
            .ToList();
        if (forceReparse) _log?.Invoke($"Full rescan: re-parsing all {pending.Count} rows (including successful ones)");
        // 即使本轮没有待解析行也要走一遍：图标引用计数清理、LRU 兜底、图标解码统计行都在里面
        // （体检报告 P2-4：解析 0 的轮次原先整段跳过，统计行与清理都不发生）。
        _saveCount = 0;
        _saveFailed = 0;
        _saveAborted = 0;
        _saveAbortedBatches = 0;
        _consecutiveSaveFailures = 0;
        _saveRetryDeadline = Environment.TickCount64 + SaveRetryBudgetMs;
        ParsePending(pending, progress, ct, result);
        result.SaveCount = _saveCount;
        result.SaveFailed = _saveFailed;
        result.SaveAborted = _saveAbortedBatches;

        result.ParsePhaseMs = parseSw.ElapsedMilliseconds;
        result.TotalMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>供调用方在"preparation stage"先问一句"Can the library be written?"（2 s 内给结论，不拖住启动）。</summary>
    internal string? ProbeDatabase() => TryWriteProbe();

    /// <summary>写探测：能写返回 null，不能写返回原因。只动 Settings 一个 key 并回滚。</summary>
    private string? TryWriteProbe()
    {
        try
        {
            // 探测用**自己的短上限**（等锁 + SQLite busy 都 2 s）：主连接的 15 s 是给正常落库留的窗口，
            // 探测等那么久只会拖住启动（审核 P2-3）。
            _db.Write(conn =>
            {
                // 真 IMMEDIATE（deferred:false）：探测要当场抢写锁。默认的 deferred 事务要到
                // INSERT 才抢，与 docs/03 §3.4 的"BEGIN IMMEDIATE"及"milliseconds"口径不符。
                using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES ('Scan.WriteProbe', '1');";
                cmd.ExecuteNonQuery();
                tx.Rollback();   // 不留下任何痕迹
                return 0;
            }, ProbeBusyTimeoutMs, ProbeBusyTimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    // ── 列表阶段 ──────────────────────────────────────────────────────────────

    private static List<ScanFile> Enumerate(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,                 // 单个目录无权限：跳过并继续（docs/03 §3.1）
            AttributesToSkip = FileAttributes.ReparsePoint, // 跳过 junction / symlink，防止目录环
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false,
        };

        var list = new List<ScanFile>();
        foreach (var path in Directory.EnumerateFiles(rootPath, "*.ipa", options))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var identity = FileIdentityReader.TryRead(path, out var id) ? id : (FileIdentity?)null;
                list.Add(new ScanFile(
                    path,
                    ScanPaths.Normalize(path),
                    info.Name,
                    identity,
                    info.Length,
                    UtcText.FormatUtc(info.LastWriteTimeUtc)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不到：跳过，不影响整体
            }

            if (list.Count % 200 == 0) progress?.Report(new ScanProgress("Enumerate", list.Count, 0));
        }

        progress?.Report(new ScanProgress("Enumerate", list.Count, list.Count));
        return list;
    }

    /// <summary>对账（docs/03 §3.3 七步 + §3.3.1 恢复映射）。返回需要落库的行。</summary>
    private List<PackageRow> Reconcile(long rootId, List<ScanFile> files, List<PackageRow> rows, string now, ScanResult result)
    {
        var toSave = new List<PackageRow>();
        var filesByPath = new Dictionary<string, ScanFile>(StringComparer.OrdinalIgnoreCase);
        var filesByIdentity = new Dictionary<(ulong, ulong, ulong), ScanFile>();
        foreach (var file in files)
        {
            filesByPath[file.NormalizedPath] = file;
            if (file.Identity is { } id) filesByIdentity[(id.VolumeSerial, id.FileIdHigh, id.FileIdLow)] = file;
        }

        var used = new HashSet<ScanFile>();
        var matched = new List<(PackageRow Row, ScanFile File, bool ByPath)>();
        var remaining = new List<PackageRow>();

        // ① 身份匹配（FileId 按三列，PathFallback 按归一化路径）
        foreach (var row in rows)
        {
            ScanFile? file = null;
            if (row.IdentityKind == IdentityKind.FileId
                && row.VolumeSerial is { } volume && row.FileIdHigh is { } high && row.FileIdLow is { } low)
            {
                filesByIdentity.TryGetValue((volume, high, low), out file);
            }
            else if (row.NormalizedPath is { } normalized)
            {
                filesByPath.TryGetValue(normalized, out file);
            }

            // 身份种类不同 → 不是身份匹配（留给 ② 的翻转分支：删除 + 新增，docs/09 §3.1）
            if (file is not null && file.Kind != row.IdentityKind) file = null;

            if (file is not null && used.Add(file)) matched.Add((row, file, false));
            else remaining.Add(row);
        }

        // ② 剩下的按路径匹配：ID 变了 = 替换；身份种类变了 = 翻转（删除 + 新增）
        var unmatchedRows = new List<PackageRow>();
        foreach (var row in remaining)
        {
            var candidatePath = row.NormalizedPath ?? ScanPaths.Normalize(row.FilePath);
            if (filesByPath.TryGetValue(candidatePath, out var file) && !used.Contains(file))
            {
                if (file.Kind != row.IdentityKind)
                {
                    row.ExistsFlag = false;
                    toSave.Add(row);
                    result.IdentityFlips++;
                    unmatchedRows.Add(row);   // 文件留给"New"分支
                    continue;
                }

                used.Add(file);
                matched.Add((row, file, true));
            }
            else
            {
                unmatchedRows.Add(row);
            }
        }

        // ③ 已匹配行：路径 / 大小 / mtime / 替换判定
        foreach (var (row, file, byPath) in matched)
        {
            var pathChanged = !string.Equals(row.FilePath, file.Path, StringComparison.Ordinal);
            row.FilePath = file.Path;
            row.FileName = file.FileName;
            row.NormalizedPath = file.NormalizedPath;
            row.LastSeenUtc = now;

            var wasMissing = !row.ExistsFlag;
            row.ExistsFlag = true;

            if (byPath)
            {
                // 同路径、ID 变了 / 路径兜底命中 → 替换（保留 Id，解析阶段 +1）
                ApplyIdentity(row, file);
                MarkPending(row);
                row.FileSize = file.Size;
                row.FileMtimeUtc = file.MtimeUtc;
                result.Replaced++;
                toSave.Add(row);
                continue;
            }

            var sameSize = row.FileSize == file.Size;
            var sameMtime = string.Equals(row.FileMtimeUtc, file.MtimeUtc, StringComparison.Ordinal);
            if (sameSize && sameMtime)
            {
                result.Unchanged++;
                if (pathChanged) result.Moved++;
                if (pathChanged || wasMissing) toSave.Add(row);
                continue;
            }

            ApplyIdentity(row, file);
            if (IsReplacement(row, file))
            {
                MarkPending(row);
                result.Replaced++;
            }
            else
            {
                result.Touched++;   // 改包但不换应用：只更新大小 / mtime，不重解析（docs/03 §3.2.1）
            }

            row.FileSize = file.Size;
            row.FileMtimeUtc = file.MtimeUtc;
            toSave.Add(row);
        }

        // ④（已删除）原方案 A 的"SHA1 ratio within the same size bucket"恢复映射：内容哈希已砍，不再有恢复匹配。
        //    同路径回来的文件仍在 ② 里按路径复用同一行（大小/mtime 变了就重解析），不会每次删/放都新增两行。

        // ⑤ 仍未匹配的行 → ExistsFlag = 0（保留行，队列可能引用）
        foreach (var row in unmatchedRows)
        {
            if (!row.ExistsFlag) continue;
            row.ExistsFlag = false;
            result.Missing++;
            toSave.Add(row);
        }

        // ⑥ 仍未匹配的文件 → 插新行
        foreach (var file in files)
        {
            if (used.Contains(file)) continue;
            toSave.Add(NewRow(rootId, file, now));
            used.Add(file);
            result.Added++;
        }

        return toSave;
    }

    private static bool IsReplacement(PackageRow row, ScanFile file)
    {
        if (string.IsNullOrWhiteSpace(row.BundleId)) return true;   // 从没解析过 → 当替换
        try
        {
            var info = IpaReader.Read(file.Path);
            if (info.Error is not null) return true;                // plist 核对失败 → 按替换（宁可重解析）
            return !string.Equals(info.BundleId, row.BundleId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static void ApplyIdentity(PackageRow row, ScanFile file)
    {
        if (file.Identity is { } id)
        {
            row.IdentityKind = IdentityKind.FileId;
            row.VolumeSerial = id.VolumeSerial;
            row.FileIdHigh = id.FileIdHigh;
            row.FileIdLow = id.FileIdLow;
        }
        else
        {
            // 身份种类不同时不会走到这里（翻转走删除 + 新增），这里只兜底
            row.IdentityKind = IdentityKind.PathFallback;
            row.VolumeSerial = null;
            row.FileIdHigh = null;
            row.FileIdLow = null;
        }
    }

    private static void MarkPending(PackageRow row)
    {
        row.ParseState = ParseStates.Pending;
        row.ParseError = null;
        row.DisplayName = null;
        row.BundleId = null;
        row.Version = null;
        row.Build = null;
        row.MinOs = null;
        row.AppDirName = null;
        row.ContentHash = null;   // 内容变了，旧哈希不能再用

        // M3 深解析列也要清：否则替换后解析失败的行会带着上一个包的 Arch / 体积 / 图标写回库（体检报告 P1-7）。
        row.Sdk = null;
        row.DeviceFamily = null;
        row.RequiredCaps = null;
        row.Arch = null;
        row.CompressedSize = 0;
        row.UncompressedSize = 0;
        row.PayloadSize = 0;
        row.AppSize = 0;
        row.ExecutableSize = 0;
        row.FrameworksSize = 0;
        row.PlugInsSize = 0;
        row.IconKey = null;
        row.LaunchImageJson = null;
        row.OriginKind = null;
    }

    private static PackageRow NewRow(long rootId, ScanFile file, string now) => new()
    {
        ScanRootId = rootId,
        FilePath = file.Path,
        FileName = file.FileName,
        NormalizedPath = file.NormalizedPath,
        IdentityKind = file.Kind,
        VolumeSerial = file.Identity?.VolumeSerial,
        FileIdHigh = file.Identity?.FileIdHigh,
        FileIdLow = file.Identity?.FileIdLow,
        FileSize = file.Size,
        FileMtimeUtc = file.MtimeUtc,
        ParseState = ParseStates.Pending,
        ParseVersion = 0,
        ExistsFlag = true,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    // ── 解析阶段 ──────────────────────────────────────────────────────────────

    private void ParsePending(List<PackageRow> pending, IProgress<ScanProgress>? progress,
        CancellationToken ct, ScanResult result)
    {
        // 统计按"This round"清零：否则手动刷新多轮后日志里的"Newly written/cached"是进程累计值（P2-1）。
        _iconNewFiles = 0;
        _iconCacheHits = 0;
        _iconFallback = 0;
        _iconNoCandidate = 0;
        _iconErrors.Clear();

        var batchSize = Math.Max(1, _options.BatchSize);
        var batch = new List<PackageRow>(batchSize);
        var batchGate = new object();
        var done = 0;

        // 解析线程**只入队**；写库与回调都在这个提交者线程上做。
        // 原因（2026-10-30 实测）：在 Parallel.ForEach 的工作线程里直接写库会出现"Stop after the first batch"。
        using var queue = new BlockingCollection<List<PackageRow>>(new ConcurrentQueue<List<PackageRow>>());
        var committer = Task.Factory.StartNew(() =>
        {
            foreach (var rows in queue.GetConsumingEnumerable())
            {
                // 连续写不进去就不再尝试：库被外部永久锁住时，一轮 15 批 × 3 次 × (15 s + 0.8 s)
                // 会拖十几分钟（审核 P2-2）。没落库的行还是 Pending，下次扫描重来。
                if (Volatile.Read(ref _saveAborted) == 1)
                {
                    Interlocked.Increment(ref _saveAbortedBatches);
                    continue;
                }

                SaveAndPublish(rows, ct);
            }
        }, TaskCreationOptions.LongRunning);
        var parseOk = 0;
        var parseFailed = 0;
        var parsed = 0;
        var parseOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.ParseConcurrency),
            CancellationToken = ct,
        };

        try
        {
        Parallel.ForEach(pending, parseOptions, row =>
        {
            ct.ThrowIfCancellationRequested();
            row.ParseVersion++;
            try
            {
                // M3：一次打开 zip 读出浅字段 + 深字段 + 体积 + 架构 + 图标候选（docs/04）
                var deep = IpaDeepReader.Read(row.FilePath);
                if (deep.Error is not null)
                {
                    row.ParseState = ParseStates.Failed;
                    row.ParseError = deep.Error;
                    ClearParseFields(row);
                    _log?.Invoke($"{row.FileName}: parse incomplete: {deep.Error}");
                    Interlocked.Increment(ref parseFailed);
                }
                else
                {
                    row.ParseState = ParseStates.Ok;
                    row.ParseError = null;
                    row.BundleId = deep.BundleId;
                    row.Version = deep.Version;
                    row.Build = deep.Build;
                    row.DisplayName = deep.DisplayName;
                    row.MinOs = deep.MinOs;
                    row.AppDirName = deep.AppDirectory;
                    row.Sdk = deep.Sdk;
                    row.DeviceFamily = deep.DeviceFamily;
                    row.RequiredCaps = deep.RequiredCapsJson;
                    row.Arch = deep.Arch;
                    row.CompressedSize = deep.CompressedSize;
                    row.UncompressedSize = deep.UncompressedSize;
                    row.PayloadSize = deep.PayloadSize;
                    row.AppSize = deep.AppSize;
                    row.ExecutableSize = deep.ExecutableSize;
                    row.FrameworksSize = deep.FrameworksSize;
                    row.PlugInsSize = deep.PlugInsSize;
                    // 内容哈希已砍（用户 2026-10-30 拍板）：不再为恢复映射整包读一遍算 SHA1。
                    row.LaunchImageJson = deep.LaunchImageJson;
                    row.OriginKind = deep.OriginKind;
                    row.IconKey = TrySaveIcon(row.FilePath, deep);

                    if (deep.Warning is { Length: > 0 }) _log?.Invoke(row.FileName + "：" + deep.Warning);
                    Interlocked.Increment(ref parseOk);
                }
            }
            catch (Exception ex)
            {
                row.ParseState = ParseStates.Failed;
                row.ParseError = ex.GetType().Name + ": " + ex.Message;
                ClearParseFields(row);   // 替换后解析失败：不能留着上一个包的深字段（体检报告 P1-7）
                _log?.Invoke($"{row.FileName}: parse incomplete: {ex.GetType().Name}: {ex.Message}");   // P2-5：原因也要进文件日志
                Interlocked.Increment(ref parseFailed);
            }

            Interlocked.Increment(ref parsed);
            var finished = Interlocked.Increment(ref done);

            List<PackageRow>? flush = null;
            lock (batchGate)
            {
                batch.Add(row);
                if (batch.Count >= batchSize)
                {
                    flush = new List<PackageRow>(batch);
                    batch.Clear();
                }
            }

            if (flush is not null) Enqueue(flush);
            if (finished % 50 == 0) progress?.Report(new ScanProgress("Parse", finished, pending.Count));
        });

        // 尾批 + 收尾
        List<PackageRow>? tail = null;
        lock (batchGate)
        {
            if (batch.Count > 0)
            {
                tail = new List<PackageRow>(batch);
                batch.Clear();
            }
        }

        if (tail is not null) Enqueue(tail);
        }
        finally
        {
            // 取消 / 异常也必须收尾提交者，否则 BlockingCollection 与消费者并发 Dispose
            // 会留下未观察 ObjectDisposedException 和滞留线程（审核报告 P1-1 实测 1–4 条异常、线程 +3/+7）。
            try { queue.CompleteAdding(); }
            catch (ObjectDisposedException) { }

            try { committer.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                _log?.Invoke("Committer thread shutdown error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        void Enqueue(List<PackageRow> rows)
        {
            try { queue.Add(rows); }
            catch (InvalidOperationException) { /* 已完成（取消路径），忽略 */ }
        }

        // 扫描结束：先按"Reference counting"清掉没人引用的旧图标（包被删 / 被替换 / 换了档位），
        // 再按上限做 LRU 兜底（容量保护）。只删缓存目录里的 png。
        if (_dataDirectory is not null && _iconOptions is not null)
        {
            // 只把"still exists"的行算作引用：已删包（ExistsFlag=0）的 IconKey 不该继续保住 png，
            // 否则删包后图标永不回收（审核报告 P1-3 实测）。
            var referenced = new HashSet<string>(
                PackageStore.LoadAll(_db)
                    .Where(r => r.ExistsFlag && r.IconKey is { Length: > 0 })
                    .Select(r => r.IconKey!),
                StringComparer.OrdinalIgnoreCase);

            var (orphans, orphanBytes) = IconCachePaths.CollectOrphans(_dataDirectory, referenced);
            if (orphans > 0)
                _log?.Invoke($"Icon cache cleanup: removed {orphans} unreferenced old files, freed {orphanBytes / 1024.0 / 1024.0:F1} MB");

            var (removedIconFiles, freedBytes) = IconCachePaths.Trim(
                _dataDirectory, _iconOptions.CacheMaxMb, referenced);
            if (removedIconFiles > 0)
                _log?.Invoke($"Icon cache over limit: LRU removed {removedIconFiles} files, freed {freedBytes / 1024.0 / 1024.0:F1} MB");

            var (_, stillBytes) = IconCachePaths.Measure(_dataDirectory);
            if (stillBytes > _iconOptions.CacheMaxMb * 1024L * 1024L)
                _log?.Invoke($"Icon cache still over limit ({stillBytes / 1024.0 / 1024.0:F1} MB > {_iconOptions.CacheMaxMb} MB): "
                             + "Remaining files are still referenced by packages; kept by reference priority");

            // LRU 只按容量淘汰、可能删掉仍被引用的 png；把指向已不存在文件的 IconKey 置空，
            // 避免 M4 列表读到死链（体检报告 P1-8）。
            var iconDir = IconCachePaths.CacheDirectory(_dataDirectory);
            if (Directory.Exists(iconDir))
            {
                var live = new HashSet<string>(
                    Directory.EnumerateFiles(iconDir, "*.png").Select(Path.GetFileNameWithoutExtension)!,
                    StringComparer.OrdinalIgnoreCase);
                var broken = PackageStore.LoadAll(_db)
                    .Where(r => r.IconKey is { Length: > 0 } key && !live.Contains(key))
                    .ToList();
                if (broken.Count > 0)
                {
                    foreach (var row in broken) row.IconKey = null;
                    PackageStore.Save(_db, broken);
                    _log?.Invoke($"Icon reference cleanup: {broken.Count} rows pointed to missing cache files; IconKey cleared");
                }
            }
        }

        // 图标解码统计（每次都打，便于 L2/L3 用同一口径对比；0 值也打，避免"No such thing = no running"的歧义）
        var iconFailures = _iconErrors.Values.Sum();
        var errorText = _iconErrors.Count == 0
            ? "None"
            : string.Join("，", _iconErrors.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "×" + kv.Value));
        _log?.Invoke($"Icon decode: new {_iconNewFiles} / cached {_iconCacheHits} / fallback {_iconFallback} / " +
                     $"no candidate {_iconNoCandidate} / failed {iconFailures} ({errorText})");

        result.ParseOk = parseOk;
        result.ParseFailed = parseFailed;
        result.Parsed = parsed;
        progress?.Report(new ScanProgress("Parse", pending.Count, pending.Count));
    }

    /// <summary>
    /// 落库一批并投递给调用方（M4-a，在提交者线程上调用）。
    /// 失败按"3 attempts / 800 ms interval"重试（用户 2026-10-30 拍板，给外部持锁留窗口）；
    /// 重试只针对"Maybe just wait a moment"的错误，并且可取消、有总预算（审核 P2-1 / P2-2）。
    /// </summary>
    private void SaveAndPublish(List<PackageRow> rows, CancellationToken ct)
    {
        var attempt = SaveWithRetry(
            () => PackageStore.Save(_db, rows),
            $"{rows.Count} rows",
            _log,
            ct,
            Volatile.Read(ref _saveRetryDeadline),
            SaveRetryAttempts,
            SaveRetryDelayMs);

        switch (attempt.Status)
        {
            case SaveAttemptStatus.Cancelled:
                return;   // 关窗中途放弃：不计数、不上屏（行仍是 Pending，下次扫描重来）
            case SaveAttemptStatus.Saved:
                Interlocked.Increment(ref _saveCount);
                Volatile.Write(ref _consecutiveSaveFailures, 0);
                break;
            default:
                // 整批回滚（行留 Pending），但要让它**可见**并计数，
                // 不能一边报"Parse N"一边其实没落库（审核报告 P1-2）。
                Interlocked.Increment(ref _saveFailed);
                _log?.Invoke($"Failed to save parse batch ({rows.Count} rows, {attempt.Attempts} attempts, rolled back): " +
                             $"{attempt.Error?.GetType().Name}: {attempt.Error?.Message}");

                var consecutive = Interlocked.Increment(ref _consecutiveSaveFailures);
                if (consecutive >= SaveAbortAfterConsecutiveFailures && Interlocked.Exchange(ref _saveAborted, 1) == 0)
                    _log?.Invoke($"{consecutive} consecutive batch write failures: skipping remaining batches this round (database persistently unwritable; retry on next scan)");
                return;
        }

        try
        {
            _onRowsParsed?.Invoke(rows);
        }
        catch (Exception ex)
        {
            // 回调（上屏）失败不影响落库结果，单独记，不再误报成"Failed to drop into warehouse"（P3）。
            _log?.Invoke($"Parse batch display callback failed ({rows.Count} rows): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 带重试的执行（纯逻辑、可单测）：只对瞬时错误重试、可取消、有总预算。
    /// </summary>
    internal static SaveAttempt SaveWithRetry(
        Action save,
        string what,
        Action<string>? log,
        CancellationToken ct,
        long deadlineTick,
        int maxAttempts,
        int delayMs)
    {
        Exception? last = null;
        var attemptsMade = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return new SaveAttempt(SaveAttemptStatus.Cancelled, attemptsMade, last);

            attemptsMade = attempt;
            try
            {
                save();
                return new SaveAttempt(SaveAttemptStatus.Saved, attempt, null);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= maxAttempts) break;

                if (!IsRetryableSaveError(ex))
                {
                    // 库已释放 / 参数错之类，重试没意义，直接判失败（审核 P2-2）
                    log?.Invoke($"Failed to save parse batch ({what}, not retryable): {ex.GetType().Name}: {ex.Message}");
                    break;
                }

                if (Environment.TickCount64 > deadlineTick)
                {
                    log?.Invoke($"Failed to save parse batch ({what}): retry budget for this round exhausted");
                    break;
                }

                log?.Invoke($"Failed to save parse batch ({what}, attempt {attempt}/{maxAttempts}): {ex.GetType().Name}: {ex.Message}");

                // 可取消等待（硬约束 #2 不许多 Thread.Sleep）：关窗时不必等满 800 ms
                if (ct.WaitHandle.WaitOne(delayMs))
                    return new SaveAttempt(SaveAttemptStatus.Cancelled, attemptsMade, last);
            }
        }

        return new SaveAttempt(SaveAttemptStatus.Failed, attemptsMade, last);
    }

    /// <summary>这类错误"Maybe just wait a moment"（库被备份/杀软短暂占住、磁盘抖动）；其余一律不重试。</summary>
    private static bool IsRetryableSaveError(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException or IOException;

    /// <summary>把一行清成"Not parsed"（失败行不留半截元数据，避免列表显示错字段）。</summary>
    private static void ClearParseFields(PackageRow row)
    {
        row.BundleId = null;
        row.DisplayName = null;
        row.Version = null;
        row.Build = null;
        row.MinOs = null;
        row.AppDirName = null;
        row.Sdk = null;
        row.DeviceFamily = null;
        row.RequiredCaps = null;
        row.Arch = null;
        row.CompressedSize = 0;
        row.UncompressedSize = 0;
        row.PayloadSize = 0;
        row.AppSize = 0;
        row.ExecutableSize = 0;
        row.FrameworksSize = 0;
        row.PlugInsSize = 0;
        row.IconKey = null;
        row.LaunchImageJson = null;
        row.OriginKind = null;
    }

    /// <summary>
    /// 选一张图标 → 解码（CgBI 还原）→ 缩到档位 → 写 <c>data\icons\&lt;sha1&gt;-&lt;档位&gt;.png</c>。
    /// **失败不算解析失败**（docs/04 §4：用默认图标，行仍是 Ok），只返回 null 并记日志。
    /// </summary>
    private string? TrySaveIcon(string ipaPath, DeepParseResult deep)
    {
        if (_iconOptions is null || _dataDirectory is null) return null;
        if (deep.IconCandidates.Count == 0)
        {
            Interlocked.Increment(ref _iconNoCandidate);
            return null;
        }

        var candidates = deep.IconCandidates
            .Select(c => new IconCandidate(c.EntryName, c.Width, c.Height, c.IsCgbi, c.Source))
            .ToList();
        var chosen = IconSelector.Select(candidates, _iconRequest);
        if (chosen is null) return null;

        var meta = deep.IconCandidates.FirstOrDefault(c => c.EntryName == chosen.Value.EntryName);
        if (meta is null) return null;

        var bucket = _iconOptions.ResolveCacheSize(meta.MaxSide, _iconRequest.TargetPx);
        var loaded = IpaDeepReader.LoadIcon(ipaPath, meta, bucket, _jpegDecoder, out var error);
        if (loaded is null)
        {
            var kind = error?.Split(':')[0] ?? "Unknown";
            _iconErrors.AddOrUpdate(kind, 1, (_, n) => n + 1);
            _log?.Invoke(Path.GetFileName(ipaPath) + ": icon could not be decoded (" + error + "), using default icon");
            return null;
        }

        if (loaded.UsedFallback) Interlocked.Increment(ref _iconFallback);

        var key = loaded.IconKey + "-" + bucket;
        var directory = IconCachePaths.CacheDirectory(_dataDirectory);
        var filePath = Path.Combine(directory, key + ".png");
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(filePath))
            {
                Interlocked.Increment(ref _iconCacheHits);
                try { File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                // 原子写：不同包可能共享同一张图标（同 sha1），并行解析会同时写同一个文件；
                // 先写临时文件再改名，避免读到写了一半的 PNG。
                var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, loaded.PngBytes);
                try
                {
                    File.Move(temp, filePath);          // 目标已存在会抛 IOException → 别人写好了，删临时文件
                }
                catch (IOException)
                {
                    File.Delete(temp);
                }

                Interlocked.Increment(ref _iconNewFiles);
            }

            return key;
        }
        catch (Exception ex)
        {
            _log?.Invoke(Path.GetFileName(ipaPath) + ": icon save failed (" + ex.GetType().Name + "), using default icon");
            return null;
        }
    }
}
