using System.IO;
using Microsoft.Data.Sqlite;

namespace IdeviceGui.Services.Data;

/// <summary><c>ScanRoots</c>：只增 / 启停，不删行；**根目录不允许重叠**（docs/03 §3.1、§4）。</summary>
internal static class ScanRootStore
{
    public const string OverlapError = "未能添加扫描根：与本机已添加的扫描根重叠（相同 / 父目录 / 子目录）";

    public static List<ScanRootRow> List(AppDatabase db) => db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Enabled, AddedUtc FROM ScanRoots ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var list = new List<ScanRootRow>();
        while (reader.Read())
        {
            list.Add(new ScanRootRow
            {
                Id = reader.GetInt64(0),
                Path = reader.GetString(1),
                Enabled = reader.GetInt64(2) != 0,
                AddedUtc = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    });

    public static ScanRootRow? FindByPath(AppDatabase db, string path)
    {
        var normalized = Normalize(path);
        return List(db).FirstOrDefault(r =>
            string.Equals(Normalize(r.Path), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>添加根目录；重叠时返回错误、不写库。已存在则直接返回（幂等）。</summary>
    public static (ScanRootRow? Row, string? Error) Add(AppDatabase db, string path)
    {
        var full = Normalize(path);
        if (string.IsNullOrWhiteSpace(full)) return (null, "扫描目录不能为空");
        if (!Directory.Exists(full)) return (null, "目录不存在：" + full);

        var existing = List(db);
        foreach (var root in existing)
        {
            var other = Normalize(root.Path);
            if (string.Equals(other, full, StringComparison.OrdinalIgnoreCase))
            {
                if (!root.Enabled) SetEnabled(db, root.Id, true);
                return (root, null);
            }

            if (IsAncestorOrSelf(other, full) || IsAncestorOrSelf(full, other))
                return (null, OverlapError + "：" + other + " 与 " + full);
        }

        var row = db.Write(conn =>
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO ScanRoots (Path, Enabled, AddedUtc) VALUES ($path, 1, $added);";
                cmd.Parameters.AddWithValue("$path", full);
                cmd.Parameters.AddWithValue("$added", UtcText.Now());
                cmd.ExecuteNonQuery();
            }

            long id;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT last_insert_rowid();";
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }

            tx.Commit();
            return new ScanRootRow { Id = id, Path = full, Enabled = true, AddedUtc = UtcText.Now() };
        });

        return (row, null);
    }

    public static void SetEnabled(AppDatabase db, long id, bool enabled) => db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScanRoots SET Enabled = $enabled WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    });

    internal static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static bool IsAncestorOrSelf(string ancestor, string candidate) =>
        string.Equals(ancestor, candidate, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 删除扫描根：**连同该根下的包行一起删**（2026-11-02 用户定：避免库膨胀；重新加回同目录 = 重新扫描入库，Id 会变）。
    /// 返回删除的包行数。编号映射由调用方用 <see cref="ScanRootNumbers.Remove"/> 同步删。
    /// </summary>
    public static int Delete(AppDatabase db, long id) => db.Write(conn =>
    {
        using var tx = conn.BeginTransaction();

        int removedPackages;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM Packages WHERE ScanRootId = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            removedPackages = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM ScanRoots WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return removedPackages;
    });
}

/// <summary><c>Packages</c>：整行读写（B7 不需要部分更新——对账结果一次算完再落库）。</summary>
internal static class PackageStore
{
    // 列顺序同时决定 SELECT 的读出顺序与 ReadRows 里的序号，改动必须两处一起改。
    private const string Columns =
        "ScanRootId, FilePath, FileName, NormalizedPath, IdentityKind, VolumeSerial, FileIdHigh, FileIdLow, " +
        "FileSize, FileMtimeUtc, ContentHash, DisplayName, BundleId, Version, Build, MinOs, AppDirName, " +
        "Sdk, DeviceFamily, RequiredCaps, Arch, " +
        "CompressedSize, UncompressedSize, PayloadSize, AppSize, ExecutableSize, FrameworksSize, PlugInsSize, IconKey, LaunchImageJson, OriginKind, " +
        "ParseVersion, ParseState, ParseError, ExistsFlag, FirstSeenUtc, LastSeenUtc, LastInstallUtc, LastInstallUdid";

    private const string SelectSql =
        "SELECT Id, " + Columns + " FROM Packages";

    public static List<PackageRow> LoadByRoot(AppDatabase db, long scanRootId) => db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE ScanRootId = $root ORDER BY FilePath;";
        cmd.Parameters.AddWithValue("$root", scanRootId);
        using var reader = cmd.ExecuteReader();
        return ReadRows(reader);
    });

    public static List<PackageRow> LoadAll(AppDatabase db) => db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " ORDER BY ExistsFlag DESC, FilePath;";
        using var reader = cmd.ExecuteReader();
        return ReadRows(reader);
    });

    /// <summary>
    /// 删除所有 <c>ExistsFlag=0</c> 的记录（2026-11-07 用户定：只删库记录，不碰磁盘文件）。
    /// 返回删除行数；文件若之后重新出现，下一轮扫描会重新入库。
    /// </summary>
    public static int DeleteMissing(AppDatabase db) => db.Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Packages WHERE ExistsFlag = 0;";
        return cmd.ExecuteNonQuery();
    });

    /// <summary>插入（<c>Id == 0</c>）或整行更新；一次事务写完。返回写入行数。</summary>
    public static int Save(AppDatabase db, IReadOnlyList<PackageRow> rows)
    {
        if (rows.Count == 0) return 0;
        return db.Write(conn =>
        {
            using var tx = conn.BeginTransaction();
            var count = 0;
            foreach (var row in rows)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (row.Id == 0)
                {
                    using (var insert = conn.CreateCommand())
                    {
                        insert.Transaction = tx;
                        insert.CommandText = "INSERT INTO Packages (" + Columns + ") VALUES (" + Values(Columns) + ");";
                        Bind(insert, row);
                        insert.ExecuteNonQuery();
                    }

                    using (var idCmd = conn.CreateCommand())
                    {
                        idCmd.Transaction = tx;
                        idCmd.CommandText = "SELECT last_insert_rowid();";
                        row.Id = Convert.ToInt64(idCmd.ExecuteScalar());
                    }
                }
                else
                {
                    cmd.CommandText =
                        "UPDATE Packages SET " + Assignments(Columns) + " WHERE Id = $id;";
                    Bind(cmd, row);
                    cmd.Parameters.AddWithValue("$id", row.Id);
                    cmd.ExecuteNonQuery();
                }

                count++;
            }

            tx.Commit();
            return count;
        });
    }

    private static List<PackageRow> ReadRows(SqliteDataReader reader)
    {
        var list = new List<PackageRow>();
        while (reader.Read())
        {
            var row = new PackageRow
            {
                Id = reader.GetInt64(0),
                ScanRootId = reader.GetInt64(1),
                FilePath = reader.GetString(2),
                FileName = reader.GetString(3),
                NormalizedPath = NullableString(reader, 4),
                IdentityKind = reader.GetString(5) == nameof(IdentityKind.PathFallback)
                    ? IdentityKind.PathFallback
                    : IdentityKind.FileId,
                VolumeSerial = NullableULong(reader, 6),
                FileIdHigh = NullableULong(reader, 7),
                FileIdLow = NullableULong(reader, 8),
                FileSize = reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                FileMtimeUtc = NullableString(reader, 10) ?? "",
                ContentHash = NullableString(reader, 11),
                DisplayName = NullableString(reader, 12),
                BundleId = NullableString(reader, 13),
                Version = NullableString(reader, 14),
                Build = NullableString(reader, 15),
                MinOs = NullableString(reader, 16),
                AppDirName = NullableString(reader, 17),
                Sdk = NullableString(reader, 18),
                DeviceFamily = NullableString(reader, 19),
                RequiredCaps = NullableString(reader, 20),
                Arch = NullableString(reader, 21),
                CompressedSize = reader.IsDBNull(22) ? 0 : reader.GetInt64(22),
                UncompressedSize = reader.IsDBNull(23) ? 0 : reader.GetInt64(23),
                PayloadSize = reader.IsDBNull(24) ? 0 : reader.GetInt64(24),
                AppSize = reader.IsDBNull(25) ? 0 : reader.GetInt64(25),
                ExecutableSize = reader.IsDBNull(26) ? 0 : reader.GetInt64(26),
                FrameworksSize = reader.IsDBNull(27) ? 0 : reader.GetInt64(27),
                PlugInsSize = reader.IsDBNull(28) ? 0 : reader.GetInt64(28),
                IconKey = NullableString(reader, 29),
                LaunchImageJson = NullableString(reader, 30),
                OriginKind = NullableString(reader, 31),
                ParseVersion = reader.IsDBNull(32) ? 0 : reader.GetInt32(32),
                ParseState = NullableString(reader, 33) ?? ParseStates.Pending,
                ParseError = NullableString(reader, 34),
                ExistsFlag = reader.IsDBNull(35) || reader.GetInt64(35) != 0,
                FirstSeenUtc = NullableString(reader, 36),
                LastSeenUtc = NullableString(reader, 37),
                LastInstallUtc = NullableString(reader, 38),
                LastInstallUdid = NullableString(reader, 39),
            };
            list.Add(row);
        }

        return list;
    }

    private static string[] SplitColumns(string columns) => columns.Split(',', StringSplitOptions.TrimEntries);

    private static string Values(string columns) =>
        string.Join(", ", SplitColumns(columns).Select(c => "$" + c));

    private static string Assignments(string columns) =>
        string.Join(", ", SplitColumns(columns).Select(c => c + " = $" + c));

    private static void Bind(SqliteCommand cmd, PackageRow row)
    {
        cmd.Parameters.AddWithValue("$ScanRootId", row.ScanRootId);
        cmd.Parameters.AddWithValue("$FilePath", row.FilePath);
        cmd.Parameters.AddWithValue("$FileName", row.FileName);
        cmd.Parameters.AddWithValue("$NormalizedPath", (object?)row.NormalizedPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$IdentityKind", row.IdentityKind.ToString());
        cmd.Parameters.AddWithValue("$VolumeSerial", (object?)RowToLong(row.VolumeSerial) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$FileIdHigh", (object?)RowToLong(row.FileIdHigh) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$FileIdLow", (object?)RowToLong(row.FileIdLow) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$FileSize", row.FileSize);
        cmd.Parameters.AddWithValue("$FileMtimeUtc", row.FileMtimeUtc);
        cmd.Parameters.AddWithValue("$ContentHash", (object?)row.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$DisplayName", (object?)row.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$BundleId", (object?)row.BundleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Version", (object?)row.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Build", (object?)row.Build ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$MinOs", (object?)row.MinOs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$AppDirName", (object?)row.AppDirName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Sdk", (object?)row.Sdk ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$DeviceFamily", (object?)row.DeviceFamily ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$RequiredCaps", (object?)row.RequiredCaps ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Arch", (object?)row.Arch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$CompressedSize", row.CompressedSize);
        cmd.Parameters.AddWithValue("$UncompressedSize", row.UncompressedSize);
        cmd.Parameters.AddWithValue("$PayloadSize", row.PayloadSize);
        cmd.Parameters.AddWithValue("$AppSize", row.AppSize);
        cmd.Parameters.AddWithValue("$ExecutableSize", row.ExecutableSize);
        cmd.Parameters.AddWithValue("$FrameworksSize", row.FrameworksSize);
        cmd.Parameters.AddWithValue("$PlugInsSize", row.PlugInsSize);
        cmd.Parameters.AddWithValue("$IconKey", (object?)row.IconKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$LaunchImageJson", (object?)row.LaunchImageJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$OriginKind", (object?)row.OriginKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ParseVersion", row.ParseVersion);
        cmd.Parameters.AddWithValue("$ParseState", row.ParseState);
        cmd.Parameters.AddWithValue("$ParseError", (object?)row.ParseError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ExistsFlag", row.ExistsFlag ? 1 : 0);
        cmd.Parameters.AddWithValue("$FirstSeenUtc", (object?)row.FirstSeenUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$LastSeenUtc", (object?)row.LastSeenUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$LastInstallUtc", (object?)row.LastInstallUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$LastInstallUdid", (object?)row.LastInstallUdid ?? DBNull.Value);
    }

    private static long? RowToLong(ulong? value) => value is null ? null : unchecked((long)value.Value);

    private static ulong? NullableULong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : unchecked((ulong)reader.GetInt64(ordinal));

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

/// <summary><c>Devices</c>：设备刷新时写最后快照（M2 只写刷新拿得到的字段）。</summary>
internal static class DeviceStore
{
    private const string UpsertSql = """
        INSERT INTO Devices (Udid, Name, ProductType, IosVersion, CpuArch, TotalBytes, FreeBytes, LastSeenUtc)
        VALUES ($udid, $name, $product, $ios, $cpu, $total, $free, $seen)
        ON CONFLICT(Udid) DO UPDATE SET
          Name = excluded.Name, ProductType = excluded.ProductType, IosVersion = excluded.IosVersion,
          CpuArch = excluded.CpuArch, TotalBytes = excluded.TotalBytes, FreeBytes = excluded.FreeBytes,
          LastSeenUtc = excluded.LastSeenUtc;
        """;

    public static void Upsert(AppDatabase db, DeviceRow row) => db.Write(conn => UpsertCore(conn, null, row));

    /// <summary>
    /// 一次事务写完多台设备（启动 / 刷新用）。外部进程锁住库时，逐台 upsert 会**每台各等满
    /// busy_timeout**（实测 3 台约 33 s 才报错），所以这里给短上限：设备快照不是关键数据，
    /// 写不进去记一条日志即可，不能拖住启动（审核 P2-3 补充实测）。
    /// </summary>
    public static int UpsertMany(AppDatabase db, IReadOnlyList<DeviceRow> rows, int waitMs, int busyTimeoutMs) =>
        db.Write(conn =>
        {
            using var tx = conn.BeginTransaction();
            var written = 0;
            foreach (var row in rows) written += UpsertCore(conn, tx, row);
            tx.Commit();
            return written;
        }, waitMs, busyTimeoutMs);

    private static int UpsertCore(Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction? tx, DeviceRow row)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpsertSql;
        cmd.Parameters.AddWithValue("$udid", row.Udid);
        cmd.Parameters.AddWithValue("$name", (object?)row.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$product", (object?)row.ProductType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ios", (object?)row.IosVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cpu", (object?)row.CpuArch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total", (object?)row.TotalBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$free", (object?)row.FreeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", (object?)row.LastSeenUtc ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static DeviceRow? Find(AppDatabase db, string udid) => db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Udid, Name, ProductType, IosVersion, CpuArch, TotalBytes, FreeBytes, LastSeenUtc FROM Devices WHERE Udid = $udid;";
        cmd.Parameters.AddWithValue("$udid", udid);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new DeviceRow
        {
            Udid = reader.GetString(0),
            Name = reader.IsDBNull(1) ? null : reader.GetString(1),
            ProductType = reader.IsDBNull(2) ? null : reader.GetString(2),
            IosVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
            CpuArch = reader.IsDBNull(4) ? null : reader.GetString(4),
            TotalBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            FreeBytes = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            LastSeenUtc = reader.IsDBNull(7) ? null : reader.GetString(7),
        };
    });
}

/// <summary><c>Settings</c>：值统一存字符串（JSON），坏值回默认（docs/09 §3.2）。</summary>
internal static class SettingsStore
{
    public const string LogToFile = "Log.ToFile";

    /// <summary>日志面板模式（<c>simple</c> / <c>detail</c>）；b3a 新增，默认简洁（docs/08 §3.1）。</summary>
    public const string LogPanelMode = "Log.PanelMode";

    public static string? GetString(AppDatabase db, string key) => db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    });

    public static bool GetBool(AppDatabase db, string key, bool fallback)
    {
        var raw = GetString(db, key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var trimmed = raw.Trim().Trim('"');
        return bool.TryParse(trimmed, out var value) ? value : fallback;
    }

    /// <summary>
    /// Settings 表 upsert 的唯一实现（T7，2026-11-03）：原来 `SetString` / `SetMany` / `LayoutStore` 三处各写一份 SQL，
    /// 改表结构要同步三处。现在只有这里一份，另外两处都调它。
    /// </summary>
    private static int Upsert(SqliteConnection conn, SqliteTransaction? tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 多键一次事务写入（审查 N7，2026-11-03）：设置窗口「应用」一次写 ~10 个键，
    /// 逐个 fire-and-forget 会丢失败、关程序可能来不及写；这里一次事务 + 调用方可 await。
    /// </summary>
    public static void SetMany(AppDatabase db, IReadOnlyList<KeyValuePair<string, string>> values)
        => db.Write(conn =>
        {
            if (values.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            foreach (var pair in values) Upsert(conn, tx, pair.Key, pair.Value);
            tx.Commit();
            return values.Count;
        });

    public static void SetString(AppDatabase db, string key, string value)
        => db.Write(conn => Upsert(conn, null, key, value));
}
