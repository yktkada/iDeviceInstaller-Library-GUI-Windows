using System.Globalization;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Parsing;

namespace IdeviceGui.ViewModels.Library;

/// <summary>排序字段（只覆盖能被"网格列头"触发的那些）。</summary>
internal enum LibrarySortField
{
    AppSize,
    Name,
    Version,
    MinOs,
    DeviceFamily,
    BundleId,
    Arch,
    FileSize,
    State,
}

internal sealed record LibrarySort(LibrarySortField Field, bool Descending);

/// <summary>
/// 资料库列表的**视图逻辑**（纯逻辑、不碰 WPF，便于单测）：
/// 过滤（名称 + BundleId 子串）→ 同 `BundleId` 合并 → 排序 → 产出「组行 + 文件行」的扁平序列。
/// <para>
/// 合并规则（docs/06 §6.5，2026-10-31 用户拍板）：只按 `BundleId` 合一行；组行显示版本范围（最小–最大）+「N 个」；
/// **组默认展开**（能看到每个文件），可收起；合并开关可关（关掉 = 一行一个文件）。
/// </para>
/// </summary>
internal sealed class LibraryView
{
    private readonly List<LibraryRow> _rows = new();        // 库行（Packages）
    private readonly List<LibraryRow> _transient = new();   // 「添加 IPA…」的临时行（未入库，见进度 §5.1 1.1）
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    private bool _mergeSameBundle = true;

    /// <summary>已见过（≥2 个成员）的组。首次出现即默认收起，覆盖"扫描中途新增组"的情况（复审 B6/B7）。</summary>
    private readonly HashSet<string> _knownGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每轮 <see cref="Build"/> 显示出来的组成员（BundleId → 成员行）。
    /// 组行状态要按成员聚合（2026-11-02），但组**收起时成员不在网格行序列里**，
    /// 只看界面行会让收起的组永远算不出状态（踩坑 C34）——所以 Build 时把成员记下来复用。
    /// </summary>
    private readonly Dictionary<string, List<LibraryRow>> _groupMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, LibraryRow> _byId = new();
    private long _groupRowSeq;

    // ── B1 三段缓存（2026-11-04）────────────────────────────────────────────
    // ① filter+sort 缓存；② group 快照缓存；③ assemble 只拼引用。
    // 展开/收起走 ToggleGroupFast，不经过 Build。
    private readonly List<LibraryRow> _sortedMaster = new();      // B2：全量已排序主序列（搜索只过滤、不重排）

    // C-全量预热（2026-11-04）：8 个排序字段 × 2 方向的行下标索引；扫描完成且数据变化时后台重建。
    // 不含 State：状态文本随队列/已装标记变化，不是纯库字段，索引会频繁失效。
    private readonly Dictionary<(LibrarySortField Field, bool Descending), int[]> _prefetchIndexes = new();
    private long _prefetchVersion = -1;

    /// <summary>预热覆盖的排序字段（State 除外，见上）。</summary>
    internal static readonly LibrarySortField[] PrefetchFields =
    {
        LibrarySortField.AppSize, LibrarySortField.FileSize, LibrarySortField.Name,
        LibrarySortField.Version, LibrarySortField.MinOs, LibrarySortField.DeviceFamily,
        LibrarySortField.BundleId, LibrarySortField.Arch,
    };

    internal static int PrefetchFieldCount => PrefetchFields.Length;

    /// <summary>当前数据版本下是否有可用的预热索引。</summary>
    internal bool HasValidPrefetch => _prefetchVersion == _dataVersion && _prefetchIndexes.Count > 0;
    private readonly HashSet<LibraryRow> _filteredSet = new();
    private readonly List<LibraryRow> _filteredSorted = new();    // 主序列按过滤集合收窄后的结果
    private readonly Dictionary<string, GroupSnapshot> _groupSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LibraryRow> _groupRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _groupIndex = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _groupOrder = new();
    private List<LibraryRow> _lastAssembled = new();
    private string _sigFilter = "";
    private HashSet<long>? _groupRowIds;
    private long _groupFilterVersion;
    private string _sigMasterSort = "";
    private string _sigGroup = "";
    private long _dataVersion;
    private long _stage1Version;
    private long _groupRowsVersion = -1;

    /// <summary>组的派生内容快照：成员引用没变、数据版本没变时直接复用，避免每轮重算版本范围/架构并集。</summary>
    private sealed class GroupSnapshot
    {
        public List<LibraryRow> Members = new();
        public string Name = "";
        public string VersionText = "";
        public string MinOs = "";
        public string DeviceFamily = "";
        public string OriginKind = "";
        public string Arch = "";
        public long AppSize;
        public long FileSize;
        public string ParseState = ParseStates.Ok;
        public string RangeAppSizeText = "—";
        public string RangeMinOsText = "—";
        public string RangeFileSizeText = "—";
        public int Count;
    }                                                                                       // 组行 Id：从 -1 递减

    /// <summary>搜索词（名称 + BundleId 子串，大小写不敏感）。</summary>
    public string SearchText { get; set; } = "";

    /// <summary>
    /// 同 BundleId 合并显示（默认开）。**勾选合并后组默认收起**（2026-10-31 用户拍板：
    /// 先看包、需要时再双击展开，不要一勾选就铺开所有文件行）。
    /// </summary>
    public bool MergeSameBundle
    {
        get => _mergeSameBundle;
        set
        {
            if (_mergeSameBundle == value) return;
            _mergeSameBundle = value;
            if (value) CollapseCurrentGroups();   // 重新开启合并：直接折叠当前所有组（不再依赖"下一次 Build"，复审 B7）
        }
    }

    /// <summary>排序（默认解压后体积降序，同体积按名称升序）。</summary>
    public LibrarySort Sort { get; set; } = new(LibrarySortField.AppSize, Descending: true);

    public IReadOnlyList<LibraryRow> Rows => _rows;

    /// <summary>
    /// 全部文件行（含被搜索过滤掉、以及组收起后不在网格里的行）。
    /// 标记刷新必须覆盖它们，否则收起的组成员带着旧状态、组行聚合会漏（2026-11-02）。
    /// </summary>
    internal IEnumerable<LibraryRow> AllFiles => _rows.Concat(_transient);

    /// <summary>当前搜索/筛选命中的文件行（未按组合并、未按展开状态裁剪）；多选「全选」口径用。</summary>
    internal IReadOnlyList<LibraryRow> FilteredFiles => _filteredSorted;

    /// <summary>行数据版本（扫描/临时行变化时递增；MainViewModel 的标记签名用它判断"行数据变了"）。</summary>
    internal long DataVersion => _dataVersion;

    /// <summary>当前用户分组的成员行 Id；null / 空 = 全部。由 MainViewModel 在切换分组时下发。</summary>
    internal HashSet<long>? GroupRowIds
    {
        get => _groupRowIds;
        set
        {
            _groupRowIds = value;
            _groupFilterVersion++;
        }
    }

    private void BumpDataVersion()
    {
        _dataVersion++;
        _prefetchIndexes.Clear();
        _prefetchVersion = -1;
    }

    /// <summary>
    /// 排序快照行（2026-11-04 审查 P2-A）：在 UI 线程把参与排序的字段值**取成不可变副本**，
    /// 后台只读快照、不碰活行对象 → 不会出现"比较器读到半新半旧值"的竞态。
    /// </summary>
    internal readonly record struct PrefetchRow(
        long Id, long AppSize, long FileSize,
        string Name, string Version, string MinOs, string DeviceFamilyText, string BundleId, string Arch);

    private static PrefetchRow CaptureRow(LibraryRow row)
        => new(row.Id, row.AppSize, row.FileSize, row.Name, row.Version, row.MinOs,
            row.DeviceFamilyText, row.BundleId, row.Arch);

    /// <summary>后台预热用：抓一份**字段值快照** + 当前数据版本（调用必须在 UI 线程）。</summary>
    internal (List<PrefetchRow> Rows, long Version) SnapshotForPrefetch()
    {
        var rows = new List<PrefetchRow>(_rows.Count + _transient.Count);
        foreach (var row in _rows) rows.Add(CaptureRow(row));
        foreach (var row in _transient) rows.Add(CaptureRow(row));
        return (rows, _dataVersion);
    }

    /// <summary>
    /// 后台构建"每字段 × 每方向"的排序下标（纯函数，不碰 UI；可取消）。
    /// 排序语义与 <see cref="SortFiles"/> 完全一致（同一比较器）。
    /// </summary>
    internal static Dictionary<(LibrarySortField Field, bool Descending), int[]> BuildPrefetchIndexes(
        IReadOnlyList<PrefetchRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<(LibrarySortField Field, bool Descending), int[]>(PrefetchFields.Length * 2);
        var count = rows.Count;
        if (count == 0) return result;

        foreach (var field in PrefetchFields)
        {
            foreach (var descending in new[] { false, true })
            {
                ct.ThrowIfCancellationRequested();
                var index = new int[count];
                for (var i = 0; i < count; i++) index[i] = i;
                var f = field;
                var d = descending;
                Array.Sort(index, (a, b) => ComparePrefetchRows(rows[a], rows[b], f, d));
                result[(field, descending)] = index;
            }
        }

        return result;
    }

    /// <summary>后台构建完成后在 UI 线程挂上索引；数据版本已变则丢弃。</summary>
    /// <summary>设置切换 Retina/R 文案：重算所有行 + 清组快照（组行文案下一轮 Build 重建）。</summary>
    internal void ReloadDeviceFamilyText(bool useRetinaText)
    {
        LibraryRow.UseRetinaText = useRetinaText;
        foreach (var row in _rows) row.RefreshDeviceFamilyText();
        foreach (var row in _transient) row.RefreshDeviceFamilyText();
        _groupSnapshots.Clear();
        _stage1Version++;
    }

    internal void ApplyPrefetch(Dictionary<(LibrarySortField Field, bool Descending), int[]> indexes, long version)
    {
        if (version != _dataVersion) return;
        _prefetchIndexes.Clear();
        foreach (var pair in indexes) _prefetchIndexes[pair.Key] = pair.Value;
        _prefetchVersion = version;
    }

    /// <summary>
    /// 最近一次 <see cref="Assemble"/> 的计数（2026-11-04 C3：替代单独的 <see cref="CountFiltered"/> 全表趟）。
    /// 口径与 CountFiltered 一致：files = 过滤后的在位文件数；groups = 会成组的 BundleId 数（仅合并开时）；missing = 缺失文件数（隐藏时 0）。
    /// </summary>
    internal (int Files, int Groups, int Missing) LastCounts { get; private set; }

    /// <summary>某个组这一轮显示的成员行（组行状态聚合用；组收起时成员不在 <see cref="Rows"/> 里）。</summary>
    internal IReadOnlyList<LibraryRow> GroupMembers(string bundleId)
        => _groupMembers.TryGetValue(bundleId, out var members) ? members : Array.Empty<LibraryRow>();

    /// <summary>库行原地同步：按 `Packages.Id` 复用对象，**不整表重建**（硬约束 #8）。</summary>
    public void ReplaceAll(IReadOnlyList<PackageRow> rows)
    {
        BumpDataVersion();
        DropTransientFor(rows);   // 同一路径已入库 → 临时行必须让位（审核 P2-1）
        var seen = new HashSet<long>();
        foreach (var row in rows)
        {
            seen.Add(row.Id);
            if (_byId.TryGetValue(row.Id, out var existing))
            {
                existing.UpdateFrom(row);
                continue;
            }

            var created = new LibraryRow { Id = row.Id, FilePath = row.FilePath, FileName = row.FileName };
            created.UpdateFrom(row);
            _byId[row.Id] = created;
            _rows.Add(created);
        }

        // 库里已经没有的行（被删掉 / 换了路径）：从视图里摘掉（行对象随之释放）
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(_rows[i].Id)) continue;
            _byId.Remove(_rows[i].Id);
            _rows.RemoveAt(i);
        }
    }

    /// <summary>把一批已解析行并进来（扫描分批上屏用）：按 `Packages.Id` 原地更新，没有的追加。</summary>
    public void Upsert(IReadOnlyList<PackageRow> rows)
    {
        BumpDataVersion();
        DropTransientFor(rows);   // 扫描分批上屏时同样要去重（审核 P2-1）
        foreach (var row in rows)
        {
            if (_byId.TryGetValue(row.Id, out var existing))
            {
                existing.UpdateFrom(row);
                continue;
            }

            var created = new LibraryRow { Id = row.Id, FilePath = row.FilePath, FileName = row.FileName };
            created.UpdateFrom(row);
            _byId[row.Id] = created;
            _rows.Add(created);
        }
    }

    /// <summary>追加一行临时（「添加 IPA…」浅解析出来的包，还没入库）。</summary>
    public void AddTransient(IpaInfo info, long syntheticId)
    {
        var row = new LibraryRow
        {
            Id = syntheticId,
            FilePath = info.Path,
            FileName = System.IO.Path.GetFileName(info.Path),
            IsTransient = true,
        };
        row.UpdateFrom(new PackageRow
        {
            Id = syntheticId,
            FilePath = info.Path,
            FileName = row.FileName,
            DisplayName = info.Name,
            BundleId = info.BundleId,
            Version = info.Version,
            Build = info.Build,
            MinOs = info.MinOs,
            AppDirName = info.AppDirectory,
            AppSize = info.AppSize,
            FileSize = info.Size,
            ParseState = info.Error is null ? ParseStates.Ok : ParseStates.Failed,
            ParseError = info.Error,
        });
        _transient.Add(row);
        BumpDataVersion();
    }

    /// <summary>库里已经有同路径的行了 → 把「添加 IPA…」的临时行摘掉（否则显示重复行、组计数虚增）。</summary>
    private void DropTransientFor(IEnumerable<PackageRow> rows)
    {
        if (_transient.Count == 0) return;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.FilePath)) paths.Add(row.FilePath);
        }

        if (paths.Count == 0) return;
        _transient.RemoveAll(row => paths.Contains(row.FilePath));
    }

    public bool ContainsPath(string path) =>
        _rows.Any(r => string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase)) ||
        _transient.Any(r => string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>切换某个组的展开 / 收起。**每次都改变状态**，返回 true 便于调用方统一处理（审核 P3-1）。</summary>
    public bool ToggleGroup(string bundleId)
    {
        if (!_collapsed.Remove(bundleId)) _collapsed.Add(bundleId);
        return true;
    }

    public bool IsCollapsed(string bundleId) => _collapsed.Contains(bundleId);

    /// <summary>把"当前已有 ≥2 个成员"的组全部折叠（开启合并 / 关→开时调用；不依赖下一次 Build）。</summary>
    private void CollapseCurrentGroups()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _rows.Concat(_transient))
        {
            if (string.IsNullOrEmpty(file.BundleId)) continue;
            counts[file.BundleId] = counts.TryGetValue(file.BundleId, out var n) ? n + 1 : 1;
        }

        foreach (var pair in counts)
        {
            if (pair.Value > 1) _collapsed.Add(pair.Key);
        }
    }

    /// <summary>产出要显示的行序列：组行 + （展开时的）文件行（B1 三段缓存，2026-11-04）。</summary>
    public List<LibraryRow> Build()
    {
        // 审查 B8（2026-11-03）：已知组 / 收起集合会话内只增不减，长期跑会缓慢膨胀。
        // 超过上限就整体清空（代价只是"已知组"回到默认展开状态，不影响数据）。
        const int maxKnownGroups = 5000;
        if (_knownGroups.Count > maxKnownGroups)
        {
            _knownGroups.Clear();
            _collapsed.Clear();
        }

        FilterAndSort();
        Regroup();
        // 调用方（多选追加隐藏已选行）不能再改内部缓存；返回副本，避免 _lastAssembled 被污染。
        return new List<LibraryRow>(Assemble());
    }

    /// <summary>
    /// 第①段：排序主序列缓存 + 过滤（B2，2026-11-04）。
    /// 全量排序只在"排序字段/方向 / 数据版本"变化时做一次；搜索只按主序列顺序过滤，不再重排。
    /// </summary>
    private void FilterAndSort()
    {
        var masterSig = string.Join("|", _dataVersion, Sort.Field, Sort.Descending);
        if (!string.Equals(masterSig, _sigMasterSort, StringComparison.Ordinal))
        {
            _sortedMaster.Clear();
            _sortedMaster.AddRange(_rows);
            foreach (var row in _transient) _sortedMaster.Add(row);

            // C-全量预热命中：直接按预排下标重排（O(n) 复制），否则走现算排序
            if (_prefetchVersion == _dataVersion &&
                _prefetchIndexes.TryGetValue((Sort.Field, Sort.Descending), out var order) &&
                order.Length == _sortedMaster.Count)
            {
                var reordered = new LibraryRow[order.Length];
                for (var i = 0; i < order.Length; i++) reordered[i] = _sortedMaster[order[i]];
                _sortedMaster.Clear();
                _sortedMaster.AddRange(reordered);
            }
            else
            {
                SortFiles(_sortedMaster);
            }

            _sigMasterSort = masterSig;
            _stage1Version++;
        }

        var filterSig = string.Join("|", _dataVersion, SearchText.Trim(), Scope.Format(), MatchMode,
            CompatFilter, DeviceProductType, DeviceVersion, DeviceHardwareModel, LibraryRow.UseRetinaText,
            AdvancedFilter.Version, AdvancedFilter.Enabled, _groupFilterVersion);
        if (!string.Equals(filterSig, _sigFilter, StringComparison.Ordinal))
        {
            _filteredSet.Clear();
            foreach (var row in _sortedMaster)
            {
                if (Matches(row)) _filteredSet.Add(row);
            }

            _sigFilter = filterSig;
            _stage1Version++;
        }

        // 主序列按过滤集合收窄（保持已排序顺序）：50k 行只是 n 次 HashSet 查询 + 引用追加。
        _filteredSorted.Clear();
        foreach (var row in _sortedMaster)
        {
            if (_filteredSet.Contains(row)) _filteredSorted.Add(row);
        }
    }

    /// <summary>B1 第②段：成组 + 组内容快照缓存（成员引用列表没变就复用快照）。</summary>
    private void Regroup()
    {
        var sig = string.Join("|", _stage1Version, _dataVersion, MergeSameBundle);
        if (string.Equals(sig, _sigGroup, StringComparison.Ordinal)) return;
        _sigGroup = sig;

        var dataChanged = _groupRowsVersion != _dataVersion;
        _groupRowsVersion = _dataVersion;
        if (dataChanged) _groupSnapshots.Clear();

        var groups = new Dictionary<string, List<LibraryRow>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        if (MergeSameBundle)
        {
            foreach (var file in _filteredSorted)
            {
                if (file.IsMissing || string.IsNullOrEmpty(file.BundleId)) continue;
                if (!groups.TryGetValue(file.BundleId, out var list))
                {
                    list = new List<LibraryRow>();
                    groups[file.BundleId] = list;
                    order.Add(file.BundleId);
                }

                list.Add(file);
            }
        }

        _groupRows.Clear();
        _groupOrder = order;
        foreach (var bundleId in order)
        {
            var members = groups[bundleId];
            if (members.Count <= 1) continue;

            if (!_groupSnapshots.TryGetValue(bundleId, out var snapshot) || !MembersEqual(snapshot.Members, members))
            {
                snapshot = BuildGroupSnapshot(bundleId, members);
                _groupSnapshots[bundleId] = snapshot;
            }

            snapshot.Members = members;
            _groupRows[bundleId] = CreateGroupRow(bundleId, snapshot, _collapsed.Contains(bundleId));
            if (_knownGroups.Add(bundleId)) _collapsed.Add(bundleId);
        }

        _groupMembers.Clear();
        foreach (var pair in groups)
        {
            if (pair.Value.Count > 1) _groupMembers[pair.Key] = pair.Value;
        }
    }

    private static bool MembersEqual(List<LibraryRow> left, List<LibraryRow> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i])) return false;
        }

        return true;
    }

    /// <summary>B1 第③段：拼装。永远执行，只做引用拷贝 + 标记位赋值，不做字符串计算。</summary>
    private List<LibraryRow> Assemble()
    {
        var result = new List<LibraryRow>(_filteredSorted.Count + 16);
        var missingMatched = 0;
        _groupIndex.Clear();

        if (MergeSameBundle)
        {
            foreach (var bundleId in _groupOrder)
            {
                if (!_groupMembers.TryGetValue(bundleId, out var members) || members.Count <= 1) continue;
                if (!_groupRows.TryGetValue(bundleId, out var groupRow)) continue;

                ApplyGroupState(groupRow, members);   // 组行状态 = 成员聚合，必须每轮重算
                var collapsed = _collapsed.Contains(bundleId);
                groupRow.IsCollapsed = collapsed;
                groupRow.IsGroupMember = false;
                groupRow.IsGroupEnd = false;
                _groupIndex[bundleId] = result.Count;
                result.Add(groupRow);

                if (collapsed)
                {
                    groupRow.IsGroupEnd = true;
                    continue;
                }

                for (var i = 0; i < members.Count; i++)
                {
                    var member = members[i];
                    member.IsGroupMember = true;
                    member.IsGroupEnd = i == members.Count - 1;
                    result.Add(member);
                }
            }
        }

        foreach (var file in _filteredSorted)
        {
            if (file.IsMissing)
            {
                missingMatched++;
                continue;
            }

            if (MergeSameBundle && !string.IsNullOrEmpty(file.BundleId) &&
                _groupMembers.TryGetValue(file.BundleId, out var grouped) && grouped.Count > 1) continue;
            file.IsGroupMember = false;
            file.IsGroupEnd = false;
            result.Add(file);
        }

        if (ShowMissingFiles)
        {
            foreach (var file in _filteredSorted)
            {
                if (!file.IsMissing) continue;
                file.IsGroupMember = false;
                file.IsGroupEnd = false;
                result.Add(file);
            }
        }

        LastCounts = (
            _filteredSorted.Count - missingMatched,
            MergeSameBundle ? _groupMembers.Count : 0,
            ShowMissingFiles ? missingMatched : 0);
        _lastAssembled = result;
        return result;
    }

    /// <summary>
    /// 展开/收起快路径（B1）：只改上次拼装好的列表，不跑 filter/sort/group。
    /// 返回**新的 List 实例**（调用方赋给 UI 绑定以触发一次刷新）。
    /// </summary>
    public List<LibraryRow> ToggleGroupFast(string bundleId)
    {
        if (!_collapsed.Remove(bundleId)) _collapsed.Add(bundleId);

        if (_lastAssembled.Count == 0 ||
            !_groupIndex.TryGetValue(bundleId, out var index) ||
            index < 0 || index >= _lastAssembled.Count ||
            !_groupMembers.TryGetValue(bundleId, out var members) ||
            members.Count == 0)
        {
            return Build();   // 缓存状态不完整：退回全量（缓存仍可命中前两段）
        }

        var groupRow = _lastAssembled[index];
        var list = new List<LibraryRow>(_lastAssembled.Count + members.Count);
        list.AddRange(_lastAssembled);

        if (!groupRow.IsCollapsed)
        {
            // 收起：先校验组行后面确实是这些成员，防御缓存状态不一致
            if (index + members.Count >= list.Count) return Build();
            for (var i = 0; i < members.Count; i++)
            {
                if (!ReferenceEquals(list[index + 1 + i], members[i])) return Build();
            }

            list.RemoveRange(index + 1, members.Count);
            groupRow.IsCollapsed = true;
            groupRow.IsGroupEnd = true;
            foreach (var member in members) member.IsGroupMember = false;
        }
        else
        {
            list.InsertRange(index + 1, members);
            groupRow.IsCollapsed = false;
            groupRow.IsGroupEnd = false;
            for (var i = 0; i < members.Count; i++)
            {
                members[i].IsGroupMember = true;
                members[i].IsGroupEnd = i == members.Count - 1;
            }
        }

        _lastAssembled = list;
        RebuildGroupIndex();
        return list;
    }

    private void RebuildGroupIndex()
    {
        _groupIndex.Clear();
        for (var i = 0; i < _lastAssembled.Count; i++)
        {
            if (_lastAssembled[i].IsGroup) _groupIndex[_lastAssembled[i].GroupBundleId] = i;
        }
    }


    /// <summary>把组行展开 / 收起的判定也作用到"组里的文件行"（界面据此缩进）。</summary>
    private GroupSnapshot BuildGroupSnapshot(string bundleId, List<LibraryRow> members)
    {
        var first = members[0];
        var appSizes = members.Where(m => m.AppSize > 0).Select(m => m.AppSize).ToList();
        var fileSizes = members.Where(m => m.FileSize > 0).Select(m => m.FileSize).ToList();
        var minOsOrdered = members.Select(m => m.MinOs)
            .Where(v => !string.IsNullOrEmpty(v))
            .OrderBy(v => v, Comparer<string>.Create(CompareVersion))
            .ToList();

        return new GroupSnapshot
        {
            Members = members,
            Name = first.Name,
            VersionText = VersionRange(members),
            MinOs = CommonValue(members.Select(m => m.MinOs)),
            DeviceFamily = CommonValue(members.Select(m => m.DeviceFamilyText)),
            OriginKind = CommonOrigin(members.Select(m => m.OriginKind)),
            Arch = LibraryRow.FormatArch(string.Join(",", members.Select(m => m.Arch))),
            AppSize = members.Sum(m => m.AppSize),
            FileSize = members.Sum(m => m.FileSize),
            ParseState = members.Any(m => m.ParseState == ParseStates.Failed) ? ParseStates.Failed : ParseStates.Ok,
            // 「解压后体积」「原始大小」「最低 iOS」显示**最小–最大**，不是合计（2026-10-31 / 2026-11-07）
            RangeAppSizeText = appSizes.Count == 0
                ? "—"
                : LibraryRow.FormatBytesRange(appSizes.Min(), appSizes.Max()),
            RangeFileSizeText = fileSizes.Count == 0
                ? "—"
                : LibraryRow.FormatBytesRange(fileSizes.Min(), fileSizes.Max()),
            RangeMinOsText = minOsOrdered.Count == 0
                ? "—"
                : RangeText(minOsOrdered[0], minOsOrdered[^1], text => text),
            Count = members.Count,
        };
    }

    /// <summary>按快照创建组行（每次 Regroup 新建行对象，内容来自快照，不再重算 Linq）。</summary>
    private LibraryRow CreateGroupRow(string bundleId, GroupSnapshot snapshot, bool collapsed)
    {
        var group = new LibraryRow
        {
            Id = --_groupRowSeq,   // 负数：与库主键（正数）区分
            FilePath = "",
            FileName = "",
            IsGroup = true,
            GroupBundleId = bundleId,
            RangeAppSizeText = snapshot.RangeAppSizeText,
            RangeFileSizeText = snapshot.RangeFileSizeText,
            RangeMinOsText = snapshot.RangeMinOsText,
        };

        group.UpdateFrom(new PackageRow
        {
            Id = group.Id,
            FilePath = "",
            FileName = "",
            DisplayName = snapshot.Name,
            BundleId = bundleId,
            Version = snapshot.VersionText,
            MinOs = snapshot.MinOs,
            DeviceFamily = "",   // 组行原始 family 为空；显示文案在 UpdateFrom 后覆盖
            LaunchImageJson = null,
            OriginKind = snapshot.OriginKind,
            Arch = snapshot.Arch,
            AppSize = snapshot.AppSize,
            FileSize = snapshot.FileSize,
            ParseState = snapshot.ParseState,
        });
        group.DeviceFamilyText = snapshot.DeviceFamily;
        group.GroupCount = snapshot.Count;
        group.IsCollapsed = collapsed;
        ApplyGroupState(group, snapshot.Members);
        return group;
    }

    /// <summary>
    /// 组行状态 = 成员聚合（2026-11-02 用户定）：**复用普通行的同一套接口**——
    /// <see cref="LibraryMarkers.ResolveGroup"/>（黄 &gt; 红 &gt; 无）+ <see cref="LibraryInstalledStates.Resolve"/>（✓ / ◐ / 空）。
    /// </summary>
    internal static void ApplyGroupState(LibraryRow group, IReadOnlyList<LibraryRow> members)
    {
        // 2026-11-04 审查 P3：合并成一次 for 循环（原先 ResolveGroup 的 LINQ 每次分配 enumerator/delegate）
        var queued = false;
        var failed = false;
        var installedMarker = false;
        var installed = 0;
        var otherVersion = 0;
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            switch (member.Marker)
            {
                case LibraryMarker.Queued: queued = true; break;
                case LibraryMarker.Failed: failed = true; break;
                case LibraryMarker.Installed: installedMarker = true; break;
            }

            if (member.InstalledState == LibraryInstalledState.Yes) installed++;
            else if (member.InstalledState == LibraryInstalledState.OtherVersion) otherVersion++;
        }

        group.Marker = LibraryMarkers.Resolve(queued, failed, installedMarker);
        group.InstalledState = LibraryInstalledStates.Resolve(installed, otherVersion, members.Count);
    }

    /// <summary>两端相同就只显示一个值，否则「最小–最大」。</summary>
    private static string RangeText<T>(T min, T max, Func<T, string> format)
    {
        var low = format(min);
        var high = format(max);
        return string.Equals(low, high, StringComparison.Ordinal) ? low : low + "–" + high;
    }

    /// <summary>搜索范围（2026-11-01：全部列都可搜；空范围 = 不按字段过滤）。</summary>
    public SearchScope Scope { get; set; } = new();

    /// <summary>匹配方式：包含 / 前缀 / 精确。</summary>
    public SearchMatchMode MatchMode { get; set; } = SearchMatchMode.Contains;

    /// <summary>是否显示"文件已删除"的行（2026-11-02 用户定：右键可隐藏/显示）。</summary>
    public bool ShowMissingFiles { get; set; } = true;

        /// <summary>兼容性下拉框（2026-11-05）：关 / 可安装 / 已适配。</summary>
    public CompatFilterMode CompatFilter { get; set; }

    /// <summary>当前设备（兼容过滤用）：ProductType / 系统版本；为空则不过滤。</summary>
    public string? DeviceProductType { get; set; }
    public string? DeviceVersion { get; set; }
    public string? DeviceHardwareModel { get; set; }

    /// <summary>高级筛选（2026-11-05）；null/未启用时不过滤。</summary>
    public AdvancedFilterState AdvancedFilter { get; set; } = new();

    /// <summary>
    /// **口径参照实现（生产已改用 <see cref="LastCounts"/>；勿删）**：等价性测试用它当参照，
    /// 改计数口径必须同时改 <c>LastCounts</c> 与本方法，否则 <c>LibraryCountsAndMarksTests</c> 会红。
    /// 计数口径（2026-11-01 修 bug）：**按过滤后的文件数**统计，与"组是否展开"无关；
    /// 组数 = 会成组的 BundleId 数（同 BundleId 文件 ≥2）。
    /// 之前按可见行数统计，展开/收起 BundleId 会让"N 个包"变化。
    /// </summary>
    public (int Files, int Groups, int Missing) CountFiltered()
    {
        var all = _rows.Concat(_transient).Where(Matches).ToList();
        if (!ShowMissingFiles) all = all.Where(f => !f.IsMissing).ToList();
        var files = all.Where(f => !f.IsMissing).ToList();
        var missing = all.Count(f => f.IsMissing);

        if (!MergeSameBundle || files.Count == 0) return (files.Count, 0, missing);

        var groups = files
            .Where(f => !string.IsNullOrEmpty(f.BundleId))
            .GroupBy(f => f.BundleId, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);

        return (files.Count, groups, missing);
    }

    private bool Matches(LibraryRow row)
    {
        if (_groupRowIds is not null && !_groupRowIds.Contains(row.Id)) return false;
        if (CompatFilter != CompatFilterMode.Off &&
            !DeviceCompat.Pass(row, CompatFilter, DeviceProductType, DeviceVersion, DeviceHardwareModel)) return false;
        if (!AdvancedFilter.Matches(row, DeviceProductType, DeviceVersion, DeviceHardwareModel)) return false;

        var text = SearchText.Trim();
        if (text.Length == 0) return true;
        if (Scope.IsEmpty) return true;

        foreach (var field in Scope.Fields)
        {
            if (SearchMatch.IsMatch(FieldText(row, field), text, MatchMode)) return true;
        }

        return false;
    }

    private static string? FieldText(LibraryRow row, SearchField field) => field switch
    {
        SearchField.Name => row.Name,
        SearchField.BundleId => row.BundleId,
        SearchField.FileName => row.FileName,
        SearchField.Version => row.Version,
        SearchField.MinOs => row.MinOs,
        SearchField.DeviceFamily => row.DeviceFamilyText,
        SearchField.Origin => row.OriginText,
        SearchField.Arch => row.ArchText + " " + row.Arch,   // 显示文案（v6/v7…）与原始值都能搜
        SearchField.FilePath => row.FilePath,
        SearchField.State => row.StateText,
        SearchField.AppSize => row.AppSizeText,
        SearchField.FileSize => row.FileSizeText,
        _ => null,
    };

    private void SortFiles(List<LibraryRow> files)
        => files.Sort((left, right) => CompareRows(left, right, Sort.Field, Sort.Descending));

    /// <summary>
    /// 快照比较器（后台预热用）：与 <see cref="CompareRows"/> **必须逐条保持一致**（同一 switch/同值 fallback），
    /// 由 <c>LibraryViewPrefetchTests</c> 对 8 字段 × 2 方向做逐行等价校验兜底。
    /// </summary>
    private static int ComparePrefetchRows(PrefetchRow left, PrefetchRow right, LibrarySortField field, bool descending)
    {
        var direction = descending ? -1 : 1;
        var primary = field switch
        {
            LibrarySortField.AppSize => left.AppSize.CompareTo(right.AppSize),
            LibrarySortField.FileSize => left.FileSize.CompareTo(right.FileSize),
            LibrarySortField.Name => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase),
            LibrarySortField.Version => CompareVersion(left.Version, right.Version),
            LibrarySortField.MinOs => CompareVersion(left.MinOs, right.MinOs),
            LibrarySortField.DeviceFamily => string.Compare(left.DeviceFamilyText, right.DeviceFamilyText, StringComparison.Ordinal),
            LibrarySortField.BundleId => string.Compare(left.BundleId, right.BundleId, StringComparison.OrdinalIgnoreCase),
            LibrarySortField.Arch => string.Compare(left.Arch, right.Arch, StringComparison.Ordinal),
            _ => 0,
        };

        if (primary != 0) return primary * direction;
        var byName = string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        return byName != 0 ? byName : left.Id.CompareTo(right.Id);
    }

    /// <summary>排序比较器（SortFiles 与后台索引共用，保证语义完全一致）。</summary>
    private static int CompareRows(LibraryRow left, LibraryRow right, LibrarySortField field, bool descending)
    {
        var direction = descending ? -1 : 1;
        {
            var primary = field switch
            {
                LibrarySortField.AppSize => left.AppSize.CompareTo(right.AppSize),
                LibrarySortField.FileSize => left.FileSize.CompareTo(right.FileSize),
                LibrarySortField.Name => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase),
                LibrarySortField.Version => CompareVersion(left.Version, right.Version),
                LibrarySortField.MinOs => CompareVersion(left.MinOs, right.MinOs),
                LibrarySortField.DeviceFamily => string.Compare(left.DeviceFamilyText, right.DeviceFamilyText, StringComparison.Ordinal),
                LibrarySortField.BundleId => string.Compare(left.BundleId, right.BundleId, StringComparison.OrdinalIgnoreCase),
                LibrarySortField.Arch => string.Compare(left.Arch, right.Arch, StringComparison.Ordinal),
                LibrarySortField.State => string.Compare(left.StateText, right.StateText, StringComparison.Ordinal),
                _ => 0,
            };

            if (primary != 0) return primary * direction;

            // 同值时按名称升序（避免顺序漂），再按 Id 保证稳定
            var byName = string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
            return byName != 0 ? byName : left.Id.CompareTo(right.Id);
        }
    }

    /// <summary>版本比较：能解析成数字段的按段比；解析不了的按字符串比。</summary>
    internal static int CompareVersion(string? left, string? right)
    {
        var a = ParseVersion(left);
        var b = ParseVersion(right);
        if (a is not null && b is not null)
        {
            var length = Math.Max(a.Length, b.Length);
            for (var i = 0; i < length; i++)
            {
                var x = i < a.Length ? a[i] : 0;
                var y = i < b.Length ? b[i] : 0;
                if (x != y) return x.CompareTo(y);
            }

            return 0;
        }

        return string.Compare(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static int[]? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().TrimStart('v', 'V').Split('.', '-', '_');
        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return null;
        }

        return values;
    }

    /// <summary>组行的版本列：最小–最大（单版本就给版本号；不可比时给"N 个版本"）。</summary>
    internal static string VersionRange(IReadOnlyList<LibraryRow> members)
    {
        var versions = members
            .Select(m => m.VersionText)
            .Where(v => !string.IsNullOrEmpty(v) && v != "—")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (versions.Count == 0) return "";
        if (versions.Count == 1) return versions[0];

        var ordered = versions.OrderBy(v => v, Comparer<string>.Create(CompareVersion)).ToList();
        var min = ordered[0];
        var max = ordered[^1];
        return min == max ? min : min + "–" + max;
    }

    /// <summary>组行里"所有成员都相同"的字段才显示值，否则显示"Mixed"。</summary>
    /// <summary>组内来源全相同才显示；混合来源不显示（用户只要三类标签，不引入“Mixed”）。</summary>
    private static string CommonOrigin(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => PackageOrigin.Mixed,
        };
    }

    private static string CommonValue(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => "Mixed",
        };
    }
}
