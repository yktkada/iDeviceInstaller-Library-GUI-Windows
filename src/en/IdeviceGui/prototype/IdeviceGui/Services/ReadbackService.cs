using System.Collections.Concurrent;
using System.Text;

namespace IdeviceGui.Services;

/// <summary>
/// 回读实现：`ideviceinstaller -u &lt;udid&gt; list --xml` → plist 数组 → BundleId + 版本。
/// <para>
/// 版本口径（总览 §6.10 实测）：**优先 `CFBundleShortVersionString`，缺失/为空时回退 `CFBundleVersion`**；
/// 两者都取不到时标记"Version unknown"（<see cref="InstalledApp.VersionKnown"/> = false），
/// 调用方只按 BundleId 判定，不得推测版本。
/// </para>
/// <para>
/// 缓存：按 UDID 存 <see cref="CacheTtl"/>；安装 / 卸载 / 刷新设备后必须 <see cref="Invalidate"/>，
/// 否则下次判定会读到过期集合。同一 UDID 的刷新用信号量串行化，避免并发拉起多个子进程。
/// </para>
/// </summary>
internal sealed class ReadbackService : IReadbackService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ICliRunner _cli;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>失效代号：Invalidate 时递增；在飞 fetch 只有代号没变才允许写回缓存（体检报告 P0-3）。</summary>
    private readonly ConcurrentDictionary<string, int> _generation = new(StringComparer.Ordinal);

    private sealed record CacheEntry(DateTime FetchedUtc, IReadOnlyList<InstalledApp> Apps);

    public ReadbackService(ICliRunner cli) => _cli = cli;

    public async Task<IReadOnlyList<InstalledApp>> ListInstalledAsync(string udid, CancellationToken ct)
    {
        if (TryGetFresh(udid, out var cached)) return cached;

        var gate = _gates.GetOrAdd(udid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双检：等锁期间别人可能已经刷过
            if (TryGetFresh(udid, out cached)) return cached;

            // 在飞 fetch 可能被并发的 Invalidate 作废：代号变了就重取一次，且绝不把旧快照写回缓存。
            for (var attempt = 0; ; attempt++)
            {
                var generation = _generation.GetOrAdd(udid, 0);

                var xml = await _cli.ListInstalledXmlAsync(udid, ct).ConfigureAwait(false);
                if (xml is null)
                    throw new InvalidOperationException("Readback command incomplete or timed out (result unknown)");

                var apps = Parse(xml);
                if (_generation.GetOrAdd(udid, 0) == generation)
                {
                    _cache[udid] = new CacheEntry(DateTime.UtcNow, apps);
                    return apps;
                }

                if (attempt >= 1) return apps;   // 连续两次被作废：结果不写缓存，交给下一次调用
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(string udid)
    {
        _generation.AddOrUpdate(udid, 1, static (_, g) => g + 1);
        _cache.TryRemove(udid, out _);
    }

    private bool TryGetFresh(string udid, out IReadOnlyList<InstalledApp> apps)
    {
        apps = Array.Empty<InstalledApp>();
        if (!_cache.TryGetValue(udid, out var entry)) return false;
        if (DateTime.UtcNow - entry.FetchedUtc > CacheTtl)
        {
            _cache.TryRemove(udid, out _);
            return false;
        }
        apps = entry.Apps;
        return true;
    }

    /// <summary>
    /// 解析 `list --xml` 的输出。<c>internal static</c> 是为了让 B6 的单元测试能直接喂样例 XML，
    /// 不需要真机与子进程。
    /// </summary>
    internal static IReadOnlyList<InstalledApp> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException("Readback output is empty");

        // ideviceinstaller / libplist 输出可能带 UTF-8 BOM，XDocument 不接受根节点前的 BOM
        var text = xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (Plist.Parse(Encoding.UTF8.GetBytes(text)) is not List<object?> list)
            throw new InvalidOperationException("Readback output is not a plist array");

        var apps = new List<InstalledApp>(list.Count);
        foreach (var element in list)
        {
            if (element is not Dictionary<string, object?> dict) continue;

            var bundleId = Str(dict, "CFBundleIdentifier");
            if (string.IsNullOrWhiteSpace(bundleId)) continue;

            var shortVersion = Str(dict, "CFBundleShortVersionString");
            var build = Str(dict, "CFBundleVersion");

            // 版本口径：ShortVersion → Build 回退；都缺 = 版本未知
            var version = !string.IsNullOrWhiteSpace(shortVersion) ? shortVersion : build;
            var known = !string.IsNullOrWhiteSpace(version);
            var displayName = Str(dict, "CFBundleDisplayName");
            if (string.IsNullOrWhiteSpace(displayName)) displayName = Str(dict, "CFBundleName");

            apps.Add(new InstalledApp(bundleId.Trim(), known ? version.Trim() : "", known, displayName.Trim()));
        }

        return apps;
    }

    private static string Str(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) && value is string text ? text : "";
}
