namespace IdeviceGui.Services.Imaging;

/// <summary>图标来源（按可信度从高到低）。</summary>
internal enum IconSource
{
    /// <summary>IPA 根目录的 <c>iTunesArtwork</c> / <c>@2x</c>（App Store 商店图标）。</summary>
    StoreArtwork = 0,

    /// <summary><c>Info.plist</c> 点名的图标（<c>CFBundleIconName</c> / <c>CFBundleIcons</c> / <c>CFBundleIconFiles</c>）。</summary>
    PlistNamed = 1,

    /// <summary><c>.app</c> 根目录下名字含 icon 的 png（含 iOS 8+ 的 <c>AppIcon*</c>）。</summary>
    RootIconName = 2,
}

/// <summary>解析出来的一张候选图标（只记元数据，不在这里解码）。</summary>
internal readonly record struct IconCandidate(
    string EntryName,
    int Width,
    int Height,
    bool IsCgbi,
    IconSource Source)
{
    public int MaxSide => Math.Max(Width, Height);
}

/// <summary>
/// 选图标时需要的显示参数。<c>DisplayPx</c> 是控件里的逻辑像素（列表 32 px），
/// <c>DpiScale</c> 是屏幕缩放（100% = 1.0、150% = 1.5、200% = 2.0）。
/// </summary>
internal readonly record struct IconRequest(
    int DisplayPx,
    double DpiScale,
    bool MatchDisplayScale = true,
    int MaxDecodePx = 1024)
{
    /// <summary>按缩放换算出来的目标像素（四舍五入），并夹到 <c>MaxDecodePx</c>。</summary>
    public int TargetPx
    {
        get
        {
            var target = (int)Math.Round(DisplayPx * DpiScale, MidpointRounding.AwayFromZero);
            if (target < 1) target = 1;
            return Math.Min(target, MaxDecodePx);
        }
    }
}

/// <summary>
/// 图标落盘档位（用户 2026-10-29 拍板：**按当前缩放比例动态调节**，缓存上限可配，默认 **2048 MB**，2026-10-30 拍板）。
/// <para>
/// 档位不是写死 32/64/128/256，而是由"Commonly used display sizes × Commonly used scaling"算出来：
/// 默认显示尺寸 = {32（列表）, 256（信息区/详情）}，默认缩放 = {1.0, 1.25, 1.5, 1.75, 2.0}，
/// 算出来向上取到 16 的倍数，得到 32 / 40 / 48 / 56 / 64 / 256 / 280 / 320 / 384 / 400 / 448 / 512 这一组。
/// </para>
/// <para>
/// **尺寸最终由 UI 决定**（M4 才定列表行高与信息区大小），所以这里只提供接口：
/// UI 层把自己的 <c>DisplayPx</c> 注册进来（<see cref="IconCacheOptions.DisplaySizes"/>），档位就跟着变。
/// </para>
/// </summary>
internal sealed class IconCacheOptions
{
    /// <summary>设置键：是否按缩放匹配源图（见 <see cref="IconRequest.MatchDisplayScale"/>）。</summary>
    public const string MatchDisplayScaleKey = "Icons.MatchDisplayScale";

    /// <summary>设置键：是否把落盘图标缩到档位（false = 存原尺寸，磁盘会明显变大）。</summary>
    public const string DownscaleCacheKey = "Icons.DownscaleCache";

    /// <summary>设置键：缓存上限（MB），默认 2048（用户 2026-10-30 拍板：默认 2 GB，可手动限制）。</summary>
    public const string CacheMaxMbKey = "Icons.CacheMaxMB";

    /// <summary>设置键：缓存档位倍数（相对 <see cref="DisplaySizes"/> 出来的目标）——留给 UI 微调用。</summary>
    public const string BucketRoundKey = "Icons.BucketRound";

    public const int DefaultCacheMaxMb = 2048;

    /// <summary>常用的图标"logical pixel"尺寸；UI 确定后再调整（列表行高 / 信息区大小）。</summary>
    public List<int> DisplaySizes { get; } = new() { 32, 256 };

    /// <summary>要覆盖的缩放比例；默认覆盖 Windows 常见的 100%–200%。</summary>
    public List<double> DpiScales { get; } = new() { 1.0, 1.25, 1.5, 1.75, 2.0 };

    /// <summary>单个图标解码上限（防内存峰值，硬约束 #8 的"Cache is capped"）。</summary>
    public int MaxDecodePx { get; set; } = 1024;

    /// <summary>缓存上限（MB）。默认 2048，设置项 <see cref="CacheMaxMbKey"/> 可改。</summary>
    public int CacheMaxMb { get; set; } = DefaultCacheMaxMb;

    /// <summary>是否缩到档位后落盘。</summary>
    public bool DownscaleCache { get; set; } = true;

    /// <summary>UI 注册自己实际用的图标显示尺寸（同一尺寸会去重）。</summary>
    public void RegisterDisplaySize(int displayPx)
    {
        if (displayPx <= 0) return;
        if (!DisplaySizes.Contains(displayPx))
        {
            DisplaySizes.Add(displayPx);
            DisplaySizes.Sort();
            _buckets = null;
        }
    }

    private int[]? _buckets;

    /// <summary>当前生效的档位（升序、去重、16 的倍数、不超过 <see cref="MaxDecodePx"/>）。</summary>
    public IReadOnlyList<int> Buckets
    {
        get
        {
            if (_buckets is not null) return _buckets;

            var set = new SortedSet<int>();
            foreach (var display in DisplaySizes)
            {
                foreach (var scale in DpiScales)
                {
                    var raw = (int)Math.Round(display * scale, MidpointRounding.AwayFromZero);
                    var bucket = Math.Max(16, (raw + 15) / 16 * 16);
                    set.Add(Math.Min(bucket, MaxDecodePx));
                }
            }

            if (set.Count == 0) set.Add(32);
            _buckets = set.ToArray();
            return _buckets;
        }
    }

    /// <summary>给定需求像素，返回应该落盘的档位（最小的 ≥ 需求的档；都不够就取最大档）。</summary>
    public int BucketFor(int targetPx)
    {
        var buckets = Buckets;
        foreach (var bucket in buckets)
        {
            if (bucket >= targetPx) return bucket;
        }

        return buckets[^1];
    }

    /// <summary>
    /// 算出**实际落盘尺寸**（用户 2026-10-29 追加口径）：
    /// <list type="number">
    /// <item>先把需求像素映射到档位（<see cref="BucketFor"/>）；</item>
    /// <item>**只有"Original image side length > this gear"时才缩小**；原图本来就不够大 → **直接用原图尺寸**，不放大、也不假装成更大的档；</item>
    /// <item><see cref="DownscaleCache"/> 关掉时一律用原图尺寸（不缩）。</item>
    /// </list>
    /// 例：原图 57 / 目标 48 → 48；原图 57 / 目标 64 → **57**（不放大）；原图 1024 / 目标 48 → 48。
    /// </summary>
    public int ResolveCacheSize(int sourcePx, int targetPx)
    {
        if (sourcePx <= 0) return Math.Max(1, targetPx);
        if (!DownscaleCache) return sourcePx;

        // 目标超出档位表时（例如将来 UI 要 1024），不要把 1024 硬压回最大档 512，
        // 否则"Give as much as you want"，显示会糊；这种情况下按需求本身（仍受 MaxDecodePx 限制）。
        var buckets = Buckets;
        var bucket = targetPx > buckets[^1] ? Math.Min(targetPx, MaxDecodePx) : BucketFor(targetPx);

        return sourcePx > bucket ? bucket : sourcePx;
    }
}

/// <summary>
/// 从候选里挑一张图标（纯逻辑，可单测）。
/// <para>
/// 规则（用户 2026-10-29 提的需求：按缩放匹配最合适的分辨率，**默认**就按这个来）：
/// <list type="number">
/// <item>按来源优先：<see cref="IconSource.StoreArtwork"/> 优先，其次 plist 点名，最后根目录 Icon*；</item>
/// <item>同一来源下，选**不小于目标、且最接近目标**的那张（"The most recent one that is enough"）。
/// 例：候选 128 / 256 / 512，目标 270 → 512（128、256 都小于目标，放大更糊）；目标 250 → 256（比 512 近得多）。</item>
/// <item>所有候选都小于目标时，选最大的那张（不可避免要放大，取最接近的）；</item>
/// <item><c>MatchDisplayScale = false</c> 时退回旧行为：每个来源里挑最大的；</item>
/// </list>
/// 这样既能避免"Always use 1024"带来的解码与内存开销，也能避免拿小图放大。
/// </para>
/// </summary>
internal static class IconSelector
{
    public static IconCandidate? Select(IReadOnlyList<IconCandidate> candidates, IconRequest request)
    {
        if (candidates.Count == 0) return null;

        var target = request.MatchDisplayScale ? request.TargetPx : 0;

        foreach (var source in new[] { IconSource.StoreArtwork, IconSource.PlistNamed, IconSource.RootIconName })
        {
            var pool = candidates
                .Where(c => c.Source == source && c.MaxSide > 0 && c.MaxSide <= request.MaxDecodePx)
                .ToList();
            if (pool.Count == 0) continue;

            if (!request.MatchDisplayScale)
                return pool.OrderByDescending(c => c.MaxSide).ThenBy(c => c.EntryName, StringComparer.Ordinal).First();

            var enough = pool.Where(c => c.MaxSide >= target).ToList();
            var chosen = enough.Count > 0
                ? enough.OrderBy(c => c.MaxSide).ThenBy(c => c.EntryName, StringComparer.Ordinal).First()
                : pool.OrderByDescending(c => c.MaxSide).ThenBy(c => c.EntryName, StringComparer.Ordinal).First();
            return chosen;
        }

        // 有候选但全超过 MaxDecodePx：退一张最小的（尽量不爆内存）
        return candidates
            .OrderBy(c => c.MaxSide)
            .ThenBy(c => c.EntryName, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
