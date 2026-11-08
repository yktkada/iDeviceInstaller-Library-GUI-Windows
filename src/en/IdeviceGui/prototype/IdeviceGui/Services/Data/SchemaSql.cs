using Microsoft.Data.Sqlite;

namespace IdeviceGui.Services.Data;

/// <summary>
/// <c>docs/09 §3.1</c> 的 schema v1 原文。**改表必须改这里 + 升 <see cref="CurrentVersion"/>**
/// （迁移流程见 docs/09 §4：没有迁移框架，靠 <c>PRAGMA user_version</c> 逐级升）。
/// </summary>
internal static class SchemaSql
{
    public const int CurrentVersion = 2;

    public const string CreateV1 = """
        -- 已知设备（含离线）：队列、上次安装与离线信息区都要引用
        CREATE TABLE Devices (
          Udid TEXT PRIMARY KEY,
          Name TEXT, ProductType TEXT, IosVersion TEXT, CpuArch TEXT,
          Color TEXT, SerialNumber TEXT, Ecid TEXT,
          TotalBytes INTEGER, FreeBytes INTEGER,
          LastSeenUtc TEXT
        );

        -- 扫描根目录：只启用/停用，不删除行（删除会留下无法对账的 Packages）
        CREATE TABLE ScanRoots (
          Id INTEGER PRIMARY KEY,
          Path TEXT NOT NULL COLLATE NOCASE UNIQUE,
          Enabled INTEGER NOT NULL DEFAULT 1,
          AddedUtc TEXT
        );

        -- IPA 资料库：一行一个文件；身份优先文件系统 ID，网络盘退化为路径
        CREATE TABLE Packages (
          Id INTEGER PRIMARY KEY,
          ScanRootId INTEGER REFERENCES ScanRoots(Id),
          FilePath TEXT NOT NULL,
          FileName TEXT NOT NULL,
          NormalizedPath TEXT,
          IdentityKind TEXT NOT NULL DEFAULT 'FileId',
          VolumeSerial INTEGER, FileIdHigh INTEGER, FileIdLow INTEGER,
          FileSize INTEGER, FileMtimeUtc TEXT,
          ContentHash TEXT,
          DisplayName TEXT, BundleId TEXT, Version TEXT, Build TEXT,
          MinOs TEXT, Sdk TEXT, DeviceFamily TEXT, RequiredCaps TEXT,
          AppDirName TEXT, Arch TEXT,
          CompressedSize INTEGER, UncompressedSize INTEGER,
          PayloadSize INTEGER, AppSize INTEGER,
          ExecutableSize INTEGER, FrameworksSize INTEGER, PlugInsSize INTEGER,
          IconKey TEXT,
          ParseVersion INTEGER NOT NULL DEFAULT 0,
          LaunchImageJson TEXT,
          OriginKind TEXT,
          ParseState TEXT NOT NULL,
          ParseError TEXT,
          ExistsFlag INTEGER NOT NULL DEFAULT 1,
          FirstSeenUtc TEXT, LastSeenUtc TEXT,
          LastInstallUtc TEXT, LastInstallUdid TEXT
        );

        -- 未完成队列：完成即删除，因此没有历史；M6 起写入
        CREATE TABLE QueueItems (
          Id INTEGER PRIMARY KEY,
          Udid TEXT NOT NULL,
          PackageId INTEGER REFERENCES Packages(Id),
          BundleId TEXT NOT NULL,
          Kind TEXT NOT NULL DEFAULT 'Install',
          OrderIndex INTEGER NOT NULL,
          Status TEXT NOT NULL,
          Note TEXT,
          UpdatedUtc TEXT,
          CHECK (
            (Kind = 'Install' AND PackageId IS NOT NULL AND BundleId <> '') OR
            (Kind = 'Uninstall' AND BundleId <> '')
          )
        );

        CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT);

        CREATE UNIQUE INDEX UX_QueueItems_Install
          ON QueueItems(Udid, PackageId) WHERE Kind = 'Install';
        CREATE UNIQUE INDEX UX_QueueItems_Uninstall
          ON QueueItems(Udid, BundleId) WHERE Kind = 'Uninstall';
        CREATE INDEX IX_QueueItems_Pump ON QueueItems(Udid, Status, OrderIndex);

        CREATE UNIQUE INDEX UX_Packages_FileId
          ON Packages(VolumeSerial, FileIdHigh, FileIdLow) WHERE IdentityKind = 'FileId';
        CREATE UNIQUE INDEX UX_Packages_PathFallback
          ON Packages(ScanRootId, NormalizedPath) WHERE IdentityKind = 'PathFallback';
        CREATE INDEX IX_Packages_RootPath ON Packages(ScanRootId, FilePath);
        CREATE INDEX IX_Packages_BundleId ON Packages(BundleId);
        CREATE INDEX IX_Packages_ParseState ON Packages(ParseState);
        """;

    /// <summary>v1 → v2：只加一列 <c>OriginKind</c>（IPA 来源），不重写数据。</summary>
    public static void UpgradeV1ToV2(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE Packages ADD COLUMN OriginKind TEXT;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA user_version = 2;";
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>建库（幂等：调用方先看 <c>user_version</c>，为 0 才执行）。</summary>
    public static void CreateV1Schema(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = CreateV1;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA user_version = " + CurrentVersion + ";";
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
