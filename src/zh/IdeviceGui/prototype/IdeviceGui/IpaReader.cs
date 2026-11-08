using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace IdeviceGui;

internal sealed class IpaInfo
{
    public required string Path { get; init; }
    public long Size { get; init; }

    /// <summary>
    /// 预计解压体积：`.app` 目录**未压缩**总量（M3 深解析的 `AppSize`，docs/07 §3.4 的空间口径）。
    /// 0 = 未知（还没深解析 / 解析失败）→ 空间检查按压缩包 ×2 保守估算。
    /// </summary>
    public long AppSize { get; init; }
    public string BundleId { get; init; } = "";
    public string Version { get; init; } = "";

    /// <summary><c>CFBundleVersion</c> 原值（B7 入库用；界面优先显示 <see cref="Version"/>）。</summary>
    public string Build { get; init; } = "";
    public string Name { get; init; } = "";
    public string MinOs { get; init; } = "";
    public string AppDirectory { get; init; } = "";
    public string? Error { get; init; }

    /// <summary>给列表显示用的一行文字。</summary>
    public string Display
    {
        get
        {
            var file = System.IO.Path.GetFileName(Path);
            if (Error is not null) return $"{file}   [未能解析: {Error}]";
            var name = string.IsNullOrEmpty(Name) ? file : Name;
            var ver = string.IsNullOrEmpty(Version) ? "?" : Version;
            var id = string.IsNullOrEmpty(BundleId) ? "?" : BundleId;
            var os = string.IsNullOrEmpty(MinOs) ? "" : $" · 要求 iOS {MinOs}+";
            return $"{name} v{ver}  ({id}){os}   ← {file}";
        }
    }

    // 让 ListBox 的 UIA 名称与界面文字一致，便于自动化测试与读屏
    public override string ToString() => Display;
}

internal static class IpaReader
{
    private static readonly Regex AppInfoPlist = new(@"^Payload/[^/]+\.app/Info\.plist$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>读取 IPA 的 Info.plist 关键字段。不抛异常，失败时通过 Error 返回原因。</summary>
    public static IpaInfo Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => AppInfoPlist.IsMatch(e.FullName));
            if (entry is null)
            {
                return new IpaInfo { Path = path, Size = SafeSize(path), Error = "未找到 Payload/*.app/Info.plist" };
            }

            byte[] bytes;
            using (var stream = entry.Open())
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }

            return ParseInfoPlist(path, bytes, entry.FullName.Split('/')[1]);
        }
        catch (Exception ex)
        {
            return new IpaInfo { Path = path, Size = SafeSize(path), Error = ex.GetType().Name + ": " + ex.Message };
        }
    }

    /// <summary>
    /// 从 Info.plist 字节直接解析字段（不出 zip、不碰磁盘）。
    /// 单独暴露是为了 B6：单元测试可以用仓库里的 golden Info.plist 断言字段，不依赖磁盘上的 IPA。
    /// </summary>
    internal static IpaInfo ParseInfoPlist(string path, byte[] infoPlistBytes, string appDirName)
    {
        if (Plist.Parse(infoPlistBytes) is not Dictionary<string, object?> dict)
        {
            return new IpaInfo { Path = path, Size = SafeSize(path), Error = "未能解析 Info.plist：结果不是字典" };
        }

        return new IpaInfo
        {
            Path = path,
            Size = SafeSize(path),
            BundleId = Str(dict, "CFBundleIdentifier"),
            Build = Str(dict, "CFBundleVersion"),
            Version = Str(dict, "CFBundleShortVersionString") is { Length: > 0 } sv ? sv : Str(dict, "CFBundleVersion"),
            Name = Str(dict, "CFBundleDisplayName") is { Length: > 0 } dn ? dn : Str(dict, "CFBundleName") is { Length: > 0 } bn ? bn : appDirName,
            MinOs = Str(dict, "MinimumOSVersion"),
            AppDirectory = appDirName,
        };
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string Str(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var v) && v is string s ? s : "";
}
