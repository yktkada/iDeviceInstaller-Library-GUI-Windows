namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// 绿色的数据源（2026-11-02 用户定）：**队列里安装成功**之后的临时提示。
/// <para>
/// - 只存内存、**不落库**（重启程序就没有）；<br/>
/// - **按设备分开记**（切到别的设备看不到这台设备的绿，切回来仍在）；<br/>
/// - 身份键 = `BundleId + 版本`（与 ✓ 的精确匹配同口径）；<br/>
/// - 只有「刷新」能清，且**只清当前选中设备**（docs/06 §6.14.6）。
/// </para>
/// </summary>
internal sealed class DeviceSuccessMarks
{
    /// <summary>单设备最多记多少条（审查 B8：会话内只增不减会缓慢膨胀；超了就整台设备清空，宁缺不乱）。</summary>
    internal const int MaxPerDevice = 4096;

    private readonly Dictionary<string, HashSet<string>> _byDevice = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每次 Add/Clear 递增（MainViewModel 的标记签名用它判断"绿色集合变过没有"）。</summary>
    internal long Version { get; private set; }

    /// <summary>身份键：BundleId（大小写不敏感）+ 入队版本（未知给空串）。</summary>
    public static string Key(string? bundleId, string? version)
        => (bundleId ?? "").Trim().ToLowerInvariant() + "\u0001" + (version ?? "").Trim();

    /// <summary>记一条「这台设备装成功了这个包」。</summary>
    public void Add(string? udid, string? bundleId, string? version)
    {
        if (string.IsNullOrWhiteSpace(udid) || string.IsNullOrWhiteSpace(bundleId)) return;

        if (!_byDevice.TryGetValue(udid, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _byDevice[udid] = set;
        }

        set.Add(Key(bundleId, version));
        if (set.Count > MaxPerDevice) set.Clear();
        Version++;   // B8 上限：极端情况下清空这台设备的记录（绿是临时提示）
    }

    /// <summary>
    /// 该设备上这个包是否装成功过（刷新之前）。
    /// **版本未知兜底**（审查 A3，2026-11-03 修）：任一侧版本为空时只按 BundleId 匹配——
    /// 入队时版本未知、重解析后版本有了，绿色不该无缘无故消失。
    /// </summary>
    public bool Contains(string? udid, string? bundleId, string? version)
    {
        if (string.IsNullOrWhiteSpace(udid) || string.IsNullOrWhiteSpace(bundleId)) return false;
        if (!_byDevice.TryGetValue(udid, out var set) || set.Count == 0) return false;

        if (set.Contains(Key(bundleId, version))) return true;

        var bundleIdKey = Key(bundleId, "");
        if (string.IsNullOrWhiteSpace(version))
        {
            // 查询侧版本未知 → 只要这个 BundleId 有任何一条绿就算（只按包名兜底）
            return set.Any(key => key.StartsWith(bundleIdKey, StringComparison.Ordinal));
        }

        // 存的时候版本未知（空串），现在查询有版本 → 兜底按 BundleId 命中
        return set.Contains(bundleIdKey);
    }

    /// <summary>清掉**一台设备**的绿色记录（「刷新」只清当前设备）。</summary>
    public void Clear(string? udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return;
        if (_byDevice.Remove(udid)) Version++;
    }

    /// <summary>清掉一台设备上**某个包**的绿（卸载成功后调用，审查 A4/B3，2026-11-03 修）。</summary>
    public void Clear(string? udid, string? bundleId)
    {
        if (string.IsNullOrWhiteSpace(udid) || string.IsNullOrWhiteSpace(bundleId)) return;
        if (!_byDevice.TryGetValue(udid, out var set)) return;

        var prefix = Key(bundleId, "");
        if (set.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal)) > 0) Version++;
        if (set.Count == 0) _byDevice.Remove(udid);
    }

    /// <summary>该设备当前记了几条（日志/测试用）。</summary>
    public int Count(string? udid)
        => !string.IsNullOrWhiteSpace(udid) && _byDevice.TryGetValue(udid, out var set) ? set.Count : 0;
}
