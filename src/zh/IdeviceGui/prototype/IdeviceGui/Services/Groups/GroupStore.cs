using System.IO;
using System.Text.Json;
using IdeviceGui.ViewModels.Library;

namespace IdeviceGui.Services.Groups;

/// <summary>用户分组（N1）：外挂 groups.json，不改主库 schema。</summary>
internal sealed class GroupFile
{
    public int Version { get; set; } = 1;
    public List<UserGroup> Groups { get; set; } = new();
}

internal sealed class UserGroup
{
    public string Name { get; set; } = "";
    public List<GroupMember> Members { get; set; } = new();
}

/// <summary>成员快照：FileId 优先、路径兜底、Bundle+版本+大小唯一兜底。</summary>
internal sealed class GroupMember
{
    public ulong? VolumeSerial { get; set; }
    public ulong? FileIdHigh { get; set; }
    public ulong? FileIdLow { get; set; }
    public string? Path { get; set; }
    public string? NormalizedPath { get; set; }
    public string? BundleId { get; set; }
    public string? Version { get; set; }
    public string? Build { get; set; }
    public long FileSize { get; set; }
}

internal sealed class GroupResolveResult
{
    public Dictionary<string, HashSet<long>> RowIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Changed { get; set; }
}

/// <summary>
/// groups.json 的读写与成员绑定。主库仍只读；分组操作只写这个外挂文件。
/// 2026-11-08 用户定：主库记录被删除 → 分组成员移除；ExistsFlag=0 仍保留（灰行）。
/// </summary>
internal static class GroupStore
{
    public const string FileName = "groups.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static GroupFile Load(string dataDirectory, out string? warning)
    {
        warning = null;
        var path = PathFor(dataDirectory);
        if (!File.Exists(path)) return new GroupFile();

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<GroupFile>(json, JsonOptions) ?? new GroupFile();
            file.Groups ??= new List<UserGroup>();
            foreach (var group in file.Groups)
            {
                group.Name = (group.Name ?? "").Trim();
                group.Members ??= new List<GroupMember>();
            }

            // 同名组只保留第一个，避免下拉框出现重名
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            file.Groups.RemoveAll(g => g.Name.Length == 0 || !seen.Add(g.Name));
            return file;
        }
        catch (Exception ex)
        {
            var backupName = "groups.bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json";
            warning = "分组文件损坏，已备份为 " + backupName + "：" + ex.GetType().Name + ": " + ex.Message;
            try { File.Copy(path, Path.Combine(dataDirectory, backupName), overwrite: false); }
            catch (Exception) { }
            return new GroupFile();
        }
    }

    public static bool Save(GroupFile file, string dataDirectory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = PathFor(dataDirectory);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>绑定成员到当前主库行；找不到的成员从 groups.json 移除（返回 Changed）。</summary>
    public static GroupResolveResult Resolve(GroupFile file, IReadOnlyList<LibraryRow> rows)
    {
        var result = new GroupResolveResult();
        // 2026-11-08 审查 P1：库为空不是“记录被删除”。库刚重建 / 只带 groups.json 到新机器时
        // 不能把用户整理的分组成员清空；等主库恢复后再绑定。
        if (rows.Count == 0) return result;

        var index = new GroupRowIndex(rows);
        foreach (var group in file.Groups)
        {
            var ids = new HashSet<long>();
            for (var i = group.Members.Count - 1; i >= 0; i--)
            {
                var row = index.Find(group.Members[i]);
                if (row is null)
                {
                    group.Members.RemoveAt(i);
                    result.Changed = true;
                    continue;
                }
                ids.Add(row.Id);
            }
            result.RowIds[group.Name] = ids;
        }

        return result;
    }

    public static LibraryRow? Match(GroupMember member, IReadOnlyList<LibraryRow> rows)
        => new GroupRowIndex(rows).Find(member);

    /// <summary>主库行的匹配索引：FileId / 规范化路径 / 快照唯一；每个行只做一次 NormalizePath。</summary>
    private sealed class GroupRowIndex
    {
        private readonly Dictionary<(ulong Volume, ulong High, ulong Low), LibraryRow> _byFileId = new();
        private readonly Dictionary<string, LibraryRow> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Bundle, string Version, string Build, long Size), List<LibraryRow>> _bySnapshot = new();

        public GroupRowIndex(IReadOnlyList<LibraryRow> rows)
        {
            foreach (var row in rows)
            {
                if (row.VolumeSerial is not null && row.FileIdHigh is not null && row.FileIdLow is not null)
                    _byFileId.TryAdd((row.VolumeSerial.Value, row.FileIdHigh.Value, row.FileIdLow.Value), row);

                var path = NormalizePath(row.NormalizedPath ?? row.FilePath);
                if (path.Length > 0) _byPath.TryAdd(path, row);

                if (!string.IsNullOrWhiteSpace(row.BundleId))
                {
                    var key = (row.BundleId!, row.Version ?? "", row.Build ?? "", row.FileSize);
                    if (!_bySnapshot.TryGetValue(key, out var list))
                    {
                        list = new List<LibraryRow>(1);
                        _bySnapshot[key] = list;
                    }
                    list.Add(row);
                }
            }
        }

        public LibraryRow? Find(GroupMember member)
        {
            if (member.VolumeSerial is not null && member.FileIdHigh is not null && member.FileIdLow is not null &&
                _byFileId.TryGetValue((member.VolumeSerial.Value, member.FileIdHigh.Value, member.FileIdLow.Value), out var byId))
                return byId;

            var normalized = NormalizePath(member.NormalizedPath ?? member.Path);
            if (normalized.Length > 0 && _byPath.TryGetValue(normalized, out var byPath)) return byPath;

            if (!string.IsNullOrWhiteSpace(member.BundleId) &&
                _bySnapshot.TryGetValue((member.BundleId, member.Version ?? "", member.Build ?? "", member.FileSize), out var snapshots) &&
                snapshots.Count == 1)
                return snapshots[0];

            return null;
        }
    }

    public static bool Contains(UserGroup group, LibraryRow row)
    {
        var index = new GroupRowIndex(new[] { row });
        return group.Members.Any(m => ReferenceEquals(index.Find(m), row));
    }

    public static int AddMembers(UserGroup group, IEnumerable<LibraryRow> rows)
    {
        var added = 0;
        var snapshot = rows.Where(r => !r.IsGroup).ToList();
        var index = new GroupRowIndex(snapshot);
        var matchedRows = group.Members
            .Select(index.Find)
            .Where(r => r is not null)
            .ToHashSet();
        foreach (var row in snapshot)
        {
            if (matchedRows.Contains(row)) continue;
            group.Members.Add(FromRow(row));
            added++;
        }
        return added;
    }

    public static int RemoveMembers(UserGroup group, IEnumerable<LibraryRow> rows)
    {
        var snapshot = rows.Where(r => !r.IsGroup).ToList();
        var index = new GroupRowIndex(snapshot);
        return group.Members.RemoveAll(m => index.Find(m) is not null);
    }

    public static GroupMember FromRow(LibraryRow row) => new()
    {
        VolumeSerial = row.VolumeSerial,
        FileIdHigh = row.FileIdHigh,
        FileIdLow = row.FileIdLow,
        Path = row.FilePath,
        NormalizedPath = row.NormalizedPath ?? NormalizePath(row.FilePath),
        BundleId = row.BundleId,
        Version = row.Version,
        Build = row.Build,
        FileSize = row.FileSize,
    };

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception) { return path.Trim(); }
    }
}
