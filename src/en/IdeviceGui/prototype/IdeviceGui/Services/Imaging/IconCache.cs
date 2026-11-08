using System.IO;

namespace IdeviceGui.Services.Imaging;

/// <summary>
/// 图标落盘的命名与清理策略（用户 2026-10-29 拍板）：
/// <list type="bullet">
/// <item>**不保留原图副本**：要原图就直接回 IPA 读；缓存里只有缩到档位的 PNG；</item>
/// <item>档位由 <see cref="IconCacheOptions.Buckets"/> 按**当前缩放比例动态**算（UI 注册显示尺寸后自动生效）；</item>
/// <item>画质无损（PNG），保证圆角透明；</item>
/// <item>上限可配（**默认 2048 MB**，2026-10-30 用户拍板：2 GB，可手动改小），超限按**最近最少使用**淘汰，
/// 先删最旧、最后再清单包的多余档位；**仍被 `Packages.IconKey` 引用的文件不淘汰**（引用优先）。</item>
/// </list>
/// <para>
/// 缓存一律放 <c>&lt;程序目录&gt;\data\icons</c>（硬约束 #7），命名 <c>&lt;sha1&gt;-&lt;档位&gt;.png</c>。
/// </para>
/// </summary>
internal static class IconCachePaths
{
    public static string CacheDirectory(string dataDirectory) => Path.Combine(dataDirectory, "icons");

    public static string FileName(string contentHash, int bucketPx) => $"{contentHash}-{bucketPx}.png";

    public static string FilePath(string dataDirectory, string contentHash, int bucketPx) =>
        Path.Combine(CacheDirectory(dataDirectory), FileName(contentHash, bucketPx));

    /// <summary>
    /// 按 LRU 把缓存压到上限以内（只删缓存目录里的 png，绝不碰别处）。
    /// 返回删除的文件数与释放的字节数。**没有可删的也不会报错**。
    /// </summary>
    public static (int RemovedFiles, long FreedBytes) Trim(string dataDirectory, int maxMb,
        IReadOnlySet<string>? referencedKeys = null)
    {
        var directory = CacheDirectory(dataDirectory);
        if (!Directory.Exists(directory)) return (0, 0);

        var limit = Math.Max(1, maxMb) * 1024L * 1024L;
        var files = new DirectoryInfo(directory)
            .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
            // Windows 默认关了 last-access 更新，LastAccessTimeUtc 可能一直等于创建时间；
            // 用 LastWriteTimeUtc 做第二排序键，至少保证"first in first out"而不是随机。
            .OrderBy(f => f.LastAccessTimeUtc)
            .ThenBy(f => f.LastWriteTimeUtc)
            .ToList();

        var total = files.Sum(f => f.Length);
        var removed = 0;
        long freed = 0;

        foreach (var file in files)
        {
            if (total <= limit) break;

            // 引用优先：仍被 Packages.IconKey 引用的 png 绝不删（用户 2026-10-30 拍板：
            // 宁可缓存超上限，也不让界面缺图）。超限由调用方记一条日志。
            if (referencedKeys is not null &&
                referencedKeys.Contains(Path.GetFileNameWithoutExtension(file.Name)))
            {
                continue;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
                freed += length;
                removed++;
            }
            catch (IOException)
            {
                // 正被占用：跳过这个，继续删下一个
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return (removed, freed);
    }

    /// <summary>
    /// **按引用计数清理**（用户 2026-10-29 拍板）：
    /// 只有缓存目录里、但**当前没有任何 `Packages` 行引用**的 png 才会被删——包括
    /// "The package was deleted""The package was replaced (sha1 changed)""The icon has changed the resolution level"这三种情况的旧文件。
    /// <para>
    /// 删除前还有两道闸：① 至少放了 <paramref name="minAgeMinutes"/> 分钟（防止删掉本轮
    /// 刚被别的扫描根/别的行写出来的文件）；② 删到 <paramref name="budget"/> 个就停（防单轮删太多卡住 UI）。
    /// 仍然只删缓存目录里的 `*.png`，绝不碰别处。
    /// </para>
    /// </summary>
    public static (int RemovedFiles, long FreedBytes) CollectOrphans(
        string dataDirectory,
        IReadOnlySet<string> referencedKeys,
        int minAgeMinutes = 10,
        int budget = 500)
    {
        var directory = CacheDirectory(dataDirectory);
        if (!Directory.Exists(directory)) return (0, 0);

        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(0, minAgeMinutes));
        var removed = 0;
        long freed = 0;

        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.png", SearchOption.TopDirectoryOnly))
        {
            if (removed >= budget) break;
            var key = Path.GetFileNameWithoutExtension(file.Name);
            if (referencedKeys.Contains(key)) continue;
            if (file.LastWriteTimeUtc > cutoff) continue;   // 太新：可能是别处刚写的，留给下一轮
            try
            {
                var length = file.Length;
                file.Delete();
                removed++;
                freed += length;
            }
            catch (IOException)
            {
                // 正被占用：跳过
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return (removed, freed);
    }

    /// <summary>统计缓存目录的当前占用（文件数 / 字节数）。</summary>
    public static (int Files, long Bytes) Measure(string dataDirectory)
    {
        var directory = CacheDirectory(dataDirectory);
        if (!Directory.Exists(directory)) return (0, 0);
        var files = new DirectoryInfo(directory).EnumerateFiles("*.png", SearchOption.TopDirectoryOnly).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }
}
