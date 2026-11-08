using System.IO;
using Microsoft.Data.Sqlite;

namespace IdeviceGui.Services.Data;

/// <summary>
/// 单库入口（docs/09 §3.1–§3.3）：<c>&lt;程序目录&gt;\data\library.db</c>，WAL +
/// <c>busy_timeout=15000</c>；**单写连接**用 <see cref="SemaphoreSlim"/> 串行化，读用独立连接。
/// <para>
/// 所有方法都是同步的（SQLite 是本地文件 IO）。调用方在后台线程里用它们，
/// **不要**在 UI 线程直接调用（硬约束 #2）。
/// </para>
/// </summary>
internal sealed class AppDatabase : IDisposable
{
    /// <summary>SQLite 忙等（用户 2026-10-30 拍板：5 s → 15 s）。</summary>
    private const int BusyTimeoutMs = 15000;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SqliteConnection? _writer;
    private bool _disposed;

    /// <summary>
    /// 扫描互斥：同一库同时只允许一轮扫描。两条并发扫描各自"Read the existing line and then write it"，会互相看不见对方
    /// 刚插入的行，从而撞 `UX_Packages_FileId` 唯一索引（B7 补充验证实测出来后才加的）。
    /// GUI 目前只在启动时扫一次，但手动重扫（M4）双击就会触发。
    /// </summary>
    internal SemaphoreSlim ScanGate { get; } = new(1, 1);

    public string DatabasePath { get; }
    public int UserVersion { get; }
    public string JournalMode { get; }
    public int BusyTimeout { get; }

    private AppDatabase(string databasePath, int userVersion, string journalMode, int busyTimeout)
    {
        DatabasePath = databasePath;
        UserVersion = userVersion;
        JournalMode = journalMode;
        BusyTimeout = busyTimeout;
    }

    /// <summary>打开（必要时建库）并校验 schema 版本。目录不存在会先创建（只写程序目录）。</summary>
    public static AppDatabase Open(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var conn = NewConnection(full, SqliteOpenMode.ReadWriteCreate);
        var journalMode = (Convert.ToString(Scalar(conn, "PRAGMA journal_mode=WAL;")) ?? "").ToLowerInvariant();
        var busyTimeout = Convert.ToInt32(Scalar(conn, "PRAGMA busy_timeout=" + BusyTimeoutMs + ";"));
        var userVersion = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));

        if (userVersion == 0)
        {
            SchemaSql.CreateV1Schema(conn);
            userVersion = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));
        }

        if (userVersion == 1)
        {
            // v1 → v2：新增 Packages.OriginKind（IPA 来源，2026-11-05）。只加列，不动已有数据。
            SchemaSql.UpgradeV1ToV2(conn);
            userVersion = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));
        }

        if (userVersion != SchemaSql.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database version {userVersion} does not match required version {SchemaSql.CurrentVersion}: " + full +
                ". Delete this file to rebuild it during development (docs/09 §4).");
        }

        return new AppDatabase(full, userVersion, journalMode, busyTimeout);
    }

    /// <summary>只读操作：独立连接，不占写锁（docs/09 §3.3）。</summary>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var conn = NewConnection(DatabasePath, SqliteOpenMode.ReadWrite);
        return work(conn);
    }

    /// <summary>
    /// 写操作：单写连接 + <see cref="SemaphoreSlim"/> 串行化（不阻塞 UI 线程，因为调用方在后台）。
    /// <para>
    /// **不可重入**：同一次调用栈里再次 <see cref="Write{T}"/> / <see cref="Checkpoint"/> / <see cref="Dispose"/>
    /// 会**立即**抛 <see cref="InvalidOperationException"/>（不是静默死锁）；跨线程等待超过
    /// <see cref="WriteWaitMs"/> 抛 <see cref="TimeoutException"/>（R1，体检报告建议 2026-10-30）。
    /// </para>
    /// </summary>
    public T Write<T>(Func<SqliteConnection, T> work) => Write(work, WriteWaitMs);

    /// <summary>
    /// 写操作，自定义等锁上限（<paramref name="waitMs"/>）；<paramref name="busyTimeoutMs"/> &gt; 0 时
    /// 临时把 SQLite 的 `busy_timeout` 调小（用完恢复）——扫描前的写探测、设备快照这类"Just forget it if you can't write it in"
    /// 的动作不该等满 15 s（审核 P2-3）。
    /// </summary>
    public T Write<T>(Func<SqliteConnection, T> work, int waitMs, int busyTimeoutMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnterWrite(waitMs);
        try
        {
            _writer ??= NewConnection(DatabasePath, SqliteOpenMode.ReadWrite);
            if (busyTimeoutMs <= 0) return work(_writer);

            // 注意（2026-10-30 实测）：**只改 `PRAGMA busy_timeout` 不够**——Microsoft.Data.Sqlite
            // 会按 `DefaultTimeout`（默认 30 s）自己重试 BUSY，命令实际能等 30 s。
            // 想要"give up soon"必须同时把连接的 DefaultTimeout 调小（秒级，最小 1 s）。
            var previousDefaultTimeout = _writer.DefaultTimeout;
            _writer.DefaultTimeout = Math.Max(1, (busyTimeoutMs + 999) / 1000);
            SetBusyTimeout(_writer, busyTimeoutMs);
            try
            {
                return work(_writer);
            }
            finally
            {
                try
                {
                    _writer.DefaultTimeout = previousDefaultTimeout;
                    SetBusyTimeout(_writer, BusyTimeout);
                }
                catch { /* 恢复失败不该盖掉原始结果 */ }
            }
        }
        finally
        {
            ExitWrite();
        }
    }

    private static void SetBusyTimeout(SqliteConnection conn, int milliseconds)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=" + milliseconds + ";";
        cmd.ExecuteNonQuery();
    }

    /// <summary>写锁等待上限；测试可临时改小以验证超时路径。</summary>
    internal static int WriteWaitMs = 15_000;

    /// <summary>当前持有写锁的托管线程号；-1 = 无人持有。</summary>
    private int _writeOwnerThreadId = -1;

    /// <summary>进入写临界区：同线程重入立即抛，跨线程有界等待。</summary>
    private void EnterWrite(int waitMs)
    {
        var tid = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _writeOwnerThreadId) == tid)
        {
            throw new InvalidOperationException(
                "Database write lock is not reentrant: a write was started again on the same call stack (nested Write / Checkpoint / Dispose will deadlock).");
        }

        if (!_writeGate.Wait(waitMs))
        {
            throw new TimeoutException(
                $"Waited more than {waitMs} ms for the database write lock: a write may be stuck, or a cross-thread lock cycle exists.");
        }

        Volatile.Write(ref _writeOwnerThreadId, tid);
    }

    /// <summary>离开写临界区。</summary>
    private void ExitWrite()
    {
        Volatile.Write(ref _writeOwnerThreadId, -1);
        _writeGate.Release();
    }

    /// <summary>把 WAL 合并回主库（关窗 / 测试里需要看 <c>-wal</c> 是否清掉时用）。</summary>
    public void Checkpoint()
    {
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            return cmd.ExecuteScalar();
        });
    }

    private static SqliteConnection NewConnection(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=" + BusyTimeoutMs + "; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    internal static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 在写回调里 Dispose 会永久挂住（R1）：交给 EnterWrite 立即抛
        EnterWrite(WriteWaitMs);
        try
        {
            _writer?.Dispose();
            _writer = null;
        }
        finally
        {
            ExitWrite();
            _writeGate.Dispose();
        }
    }
}
