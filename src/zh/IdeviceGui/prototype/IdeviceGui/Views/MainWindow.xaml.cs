using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Microsoft.Win32;

namespace IdeviceGui.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closing;

    // 三栏布局（M4-b1）：比例 + 日志栏是否隐藏 + 浮层宽度（浮层本身在 M4-b3 落地）
    private LayoutState _layout = LayoutState.Default;
    private bool _logHidden;

    /// <summary>列对象 → 稳定 key（抓配置时用；顺序按 <see cref="DataGridColumn.DisplayIndex"/>）。</summary>
    private readonly Dictionary<DataGridColumn, string> _columnKeys = new();

    // 多选模式：右键菜单切到专用菜单；关闭时恢复原来的单一模式菜单。
    private ContextMenu? _singleLibraryContextMenu;
    private ContextMenu? _multiLibraryContextMenu;

    /// <summary>
    /// CLI / AFC helper 由 <see cref="App.OnStartup"/> 先构造好再传进来：
    /// 启动失败的处理必须在"建窗口之前"完成，不能靠构造函数 throw + 全局异常兜底
    /// （否则会留下无窗口的 Dispatcher 进程并占住单实例 Mutex，见复核 D0）。
    /// </summary>
    internal MainWindow(ICliRunner cli, IAfcHelper afc, string? scanRootOverride = null)
    {
        InitializeComponent();
        _singleLibraryContextMenu = SingleLibraryContextMenu;

        _vm = new MainViewModel(cli, afc, PickIpaFiles, scanRootOverride, new DispatcherUiTimer());
        DataContext = _vm;
        GroupFilterCombo.SelectedIndex = 0;   // 启动默认「全部」
        // 分组集合重建（扫描/管理）后 ComboBox 可能被清空到 null；恢复当前选择，避免启动/刷新后显示空白。
        _vm.GroupFilterOptions.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
            {
                if (GroupFilterCombo.SelectedItem is null && _vm.GroupFilterOptions.Count > 0)
                    GroupFilterCombo.SetCurrentValue(
                        System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                        _vm.ActiveGroupName);
            }));

        // 行高可能被设置窗口"预览"改动（不经 SetLibraryRowHeight），这里统一跟一下图标列宽
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LibraryRowHeight)) ApplyIconColumnWidth();
        };

        _vm.LogLinesFlushed += OnLogFlushed;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        StartUiWatchdog();
        Loaded += async (_, _) =>
        {
            UpdateMinSize();
            // 开库后立刻套用布局与列配置，再让扫描跑（否则冷扫期间先看到默认布局再跳一下）。
            // 列配置存在库里，所以必须等开库完成才能读。
            await _vm.InitializeAsync(layout =>
            {
                ApplyLayout(layout);
                ApplyLibraryColumns();
            });
        };




    }

    /// <summary>
    /// b3b：一批日志上屏后滚一次到底。**必须异步**（DispatcherPriority.Background）——
    /// 直接在 CollectionChanged / 批量回调里改集合会触发
    /// "Cannot change ObservableCollection during a CollectionChanged event"（进度 §5.1 第 4 条）。
    /// </summary>
    private void OnLogFlushed()
    {
        if (_vm is null || _logHidden) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (LogList.Items.Count == 0) return;
            try
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
            catch
            {
                // 滚动失败不应影响日志本身
            }
        }));
    }

    // ── 三栏布局（M4-b1，docs/01 §2 / §2.1） ─────────────────────────────────

    /// <summary>套用保存的布局：比例、日志栏隐藏、浮层宽度（浮层 M4-b3 才用）。</summary>
    private void ApplyLayout(LayoutState state)
    {
        _layout = state;
        _logHidden = state.LogHidden;
        ApplyColumns();
        UpdateLogVisibility();
        UpdateMinSize();
    }

    private void ApplyColumns()
    {
        var columns = LayoutStore.Normalize(_layout.Columns);
        LeftColumn.Width = new GridLength(columns[0], GridUnitType.Star);
        MiddleColumn.Width = new GridLength(columns[1], GridUnitType.Star);
        RightColumn.Width = _logHidden ? new GridLength(0) : new GridLength(columns[2], GridUnitType.Star);
    }

    /// <summary>当前比例：日志栏隐藏时沿用上次记住的比例（否则会把 0 存进库）。</summary>
    private double[] CurrentColumns() => _logHidden
        ? _layout.Columns
        : new[] { StarValue(LeftColumn), StarValue(MiddleColumn), StarValue(RightColumn) };

    private static double StarValue(ColumnDefinition column) => column.Width.IsStar
        ? column.Width.Value
        : column.ActualWidth > 0
            ? column.ActualWidth
            : column.Width.Value;

    private void UpdateLogVisibility()
    {
        LogGroup.Visibility = _logHidden ? Visibility.Collapsed : Visibility.Visible;
        RightSplitter.Visibility = _logHidden ? Visibility.Collapsed : Visibility.Visible;
        RightColumn.MinWidth = _logHidden ? 0 : LayoutStore.MinRightWidth;
        LogToggleButton.Content = _logHidden ? "显示日志" : "隐藏日志";
    }

    private void OnToggleLogClick(object sender, RoutedEventArgs e) => ToggleLog();

    /// <summary>显示 / 隐藏日志栏（工具条按钮与 Ctrl+L 共用；最大化时的叠加层在 M4-b3 接进来）。</summary>
    private void ToggleLog()
    {
        var columns = CurrentColumns();          // 先把当前比例抓住，再切隐藏状态
        _logHidden = !_logHidden;
        _layout = _layout with { LogHidden = _logHidden, Columns = columns };
        ApplyColumns();
        UpdateLogVisibility();
        UpdateMinSize();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleLog();
            e.Handled = true;
        }
    }

    /// <summary>双击分隔条恢复默认比例 45 / 27 / 28（隐藏状态不动）。</summary>
    private void OnSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _layout = _layout with { Columns = LayoutState.Default.Columns };
        ApplyColumns();
        UpdateMinSize();
        e.Handled = true;
    }

    /// <summary>窗口最小宽度按三栏最小宽度之和 + 分隔条 + 边距算（日志隐藏时不需要右栏）。</summary>
    private void UpdateMinSize()
    {
        var columns = _logHidden
            ? LayoutStore.MinLeftWidth + LayoutStore.MinMiddleWidth
            : LayoutStore.MinLeftWidth + LayoutStore.MinMiddleWidth + LayoutStore.MinRightWidth;
        var splitters = _logHidden ? LayoutStore.SplitterWidth : LayoutStore.SplitterWidth * 2;
        MinWidth = columns + splitters + 20 /* DockPanel Margin */ + 16 /* 边框 / 标题栏余量 */;
    }

    /// <summary>
    /// 关窗前取消所有设备泵并杀子进程，确认没有残留 ideviceinstaller / AfcHelper 再退出。
    /// Closing 是同步签名，所以先取消关闭、await 清理、再真正关一次（_closing 防重入）。
    /// 布局在关窗最前面存（此时库还活着）：用户拖过的比例、隐藏状态要带到下次启动。
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            // 二次点 X：清理还没结束，继续取消关闭，避免窗口先关、后台还在用设备/库（体检报告 P1-5）
            e.Cancel = true;
            return;
        }
        _closing = true;
        e.Cancel = true;

        try
        {
            var layout = _layout with { LogHidden = _logHidden, Columns = CurrentColumns() };
            var columns = CaptureLibraryColumns();
            await Task.Run(() =>
            {
                _vm.SaveLayout(layout);
                _vm.SaveGridColumns(columns);
            });
        }
        catch
        {
            // 布局存不下来不影响关窗
        }

        try
        {
            await _vm.ShutdownAsync();
        }
        catch
        {
            // 关闭路径不再抛异常
        }

        Application.Current.Shutdown();
    }

    // ── 资料库网格（M4-b2，docs/06 §3.1） ───────────────────────────────────

    /// <summary>
    /// 按保存的配置（顺序 / 宽度）建列。列**由描述表生成**而不是写死在 XAML：
    /// 用户拖动换序后只需按 `Grid.Columns` 的顺序重建，key 稳定不认"第几列"。
    /// </summary>
    private void ApplyLibraryColumns()
    {
        var states = _vm.LoadGridColumns();
        LibraryGrid.Columns.Clear();
        _columnKeys.Clear();

        foreach (var state in states)
        {
            var column = BuildColumn(state.Key);
            if (column is null) continue;
            column.Width = new DataGridLength(state.Width, DataGridLengthUnitType.Pixel);
            column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
            var spec = LibraryColumns.Find(state.Key);
            if (spec is not null) column.MinWidth = spec.MinWidth;   // 拖不窄于设计最小宽度
            _columnKeys[column] = state.Key;
            LibraryGrid.Columns.Add(column);
        }

        ApplyIconColumnWidth();
        SyncSortIndicatorsFromViewModel();
    }

    /// <summary>图标列宽 = 行高 + 左右边距（2026-11-01：保证图标正方形并居中）。</summary>
    private void ApplyIconColumnWidth()
    {
        if (_vm is null) return;

        foreach (var column in LibraryGrid.Columns)
        {
            if (!_columnKeys.TryGetValue(column, out var key) || key != LibraryColumns.Icon) continue;
            column.Width = new DataGridLength(_vm.LibraryRowHeight + 6, DataGridLengthUnitType.Pixel);
        }
    }

    private DataGridColumn? BuildColumn(string key) => key switch
    {
        LibraryColumns.Installed => BuildInstalledColumn(),
        LibraryColumns.Icon => BuildIconColumn(),
        LibraryColumns.Name => TextColumn(key, "名称", nameof(LibraryRow.NameText)),
        LibraryColumns.Origin => TextColumn(key, "来源", nameof(LibraryRow.OriginText)),
        LibraryColumns.AppSize => TextColumn(key, "解压后体积", nameof(LibraryRow.AppSizeText), right: true),
        LibraryColumns.Version => TextColumn(key, "版本", nameof(LibraryRow.VersionText)),
        LibraryColumns.MinOs => TextColumn(key, "最低 iOS", nameof(LibraryRow.MinOsText)),
        LibraryColumns.DeviceFamily => TextColumn(key, "设备族", nameof(LibraryRow.DeviceFamilyText)),
        LibraryColumns.BundleId => TextColumn(key, "BundleId", nameof(LibraryRow.BundleId)),
        LibraryColumns.Arch => TextColumn(key, "架构", nameof(LibraryRow.ArchText)),
        LibraryColumns.FileSize => TextColumn(key, "原始大小", nameof(LibraryRow.FileSizeText), right: true),
        LibraryColumns.State => BuildStateColumn(),
        LibraryColumns.FilePath => TextColumn(key, "文件路径", nameof(LibraryRow.FilePathText),
            tooltipPath: nameof(LibraryRow.FilePath)),
        _ => null,
    };

    private static DataGridColumn TextColumn(string key, string header, string path, bool right = false, string? tooltipPath = null)
    {
        var column = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            SortMemberPath = key,
            CanUserSort = true,
        };
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(right ? 0 : 4, 0, right ? 6 : 4, 0)));
            // 2026-10-31：列窄时**截断显示 `…`** 而不是静默切字（之前组行的体积范围被裁成"9.1GB−1.92GB"看不出原因）
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            // 2026-11-02 用户定：资料库所有文字**上下居中**
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                new Binding(tooltipPath ?? path) { Mode = BindingMode.OneWay }));
            if (right) style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right));
            column.ElementStyle = style;
        }

        return column;
    }

    private DataGridColumn BuildIconColumn()
        => BuildTemplateColumn(LibraryColumns.Icon, "图标", "IconCellTemplate", cannotResize: true);

    /// <summary>已安装 ✓ 列（2026-11-01）：最前、极窄、只显示 ✓ / ◐ / 空（黑色、居中）。</summary>
    private DataGridColumn BuildInstalledColumn()
        => BuildTemplateColumn(LibraryColumns.Installed, "✓", "InstalledCellTemplate", cannotResize: false);

    private DataGridTemplateColumn BuildTemplateColumn(string key, string header, string templateKey, bool cannotResize)
    {
        var column = new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = (DataTemplate)FindResource(templateKey),
            CanUserSort = false,
            CanUserResize = !cannotResize,
            SortMemberPath = key,
        };

        // 勾列：打勾 → 整格绿底（2026-11-02 第二轮）；图标列：默认白 + 组行按状态染色；两列都不画竖线
        column.CellStyle = key switch
        {
            LibraryColumns.Installed => (Style)FindResource("LibraryInstalledCellStyle"),
            LibraryColumns.Icon => (Style)FindResource("LibraryIconCellStyle"),
            _ => null,
        };

        if (cannotResize)
        {
            // 图标列宽由行高自动决定（ApplyIconColumnWidth），不参与拖动
            column.Width = new DataGridLength(38, DataGridLengthUnitType.Pixel);
            column.MinWidth = 22;
        }

        return column;
    }

    /// <summary>状态列：正常一行字；**兼容冲突时整格红底**（M5 才可能变红，接口已预留）。</summary>
    private static DataGridColumn BuildStateColumn()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var text = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        text.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(LibraryRow.StateText)));
        text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(LibraryRow.StateTooltip)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        border.AppendChild(text);

        var style = new Style(typeof(Border));
        var trigger = new DataTrigger
        {
            Binding = new Binding(nameof(LibraryRow.IsCompatConflict)),
            Value = true,
        };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0xC7, 0xCE))));
        trigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xC0, 0x00, 0x00))));
        trigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        style.Triggers.Add(trigger);
        border.SetValue(FrameworkElement.StyleProperty, style);

        return new DataGridTemplateColumn
        {
            Header = "状态",
            CellTemplate = new DataTemplate { VisualTree = border },
            CanUserSort = true,
            SortMemberPath = LibraryColumns.State,
        };
    }

    // ── 资料库右键复制（2026-11-01 用户要求）─────────────────────────────

    /// <summary>右键先选中鼠标下的行（WPF 默认右键不改选中，直接复制会复制上一行）。</summary>
    /// <summary>
    /// 右键时命中的行（2026-11-01）：右键菜单里的复制/打开都优先用它，避免 SelectedItem 滞后导致"打开路径坏"。
    /// 审查 N6：右键空白处必须清空，否则菜单会作用到上一次右键的旧行。
    /// </summary>
    private LibraryRow? _contextRow;

    // 多选拖动选择（Explorer 风格）
    private LibraryRow? _dragCandidateRow;
    private System.Windows.Point _dragStartPoint;
    private bool _dragMoved;

    private void OnLibraryPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            _contextRow = null;
            return;
        }

        _contextRow = row.Item as LibraryRow;
        if (_contextRow?.IsAppendedSeparator == true)
        {
            _contextRow = null;
            return;
        }

        // 多选模式：右键未选中的文件行先选中它；组行永远不可选。
        if (_vm?.IsMultiSelectMode == true)
        {
            if (_contextRow is { IsGroup: false } && !_contextRow.IsMultiSelected)
                _vm.ToggleLibraryMultiSelect(_contextRow);
            _multiLibraryContextMenu = BuildInstallToMenu(
                includeSelectionActions: true, isGroupRow: _contextRow?.IsGroup == true);
            LibraryGrid.ContextMenu = _multiLibraryContextMenu;
            return;
        }

        // 单选模式：任意未选中行右键 = 先选中该行，再弹菜单（分组操作依赖 SelectedLibraryRow）。
        if (!row.IsSelected)
        {
            LibraryGrid.SelectedItem = _contextRow;
            if (_contextRow is not null) _vm!.SelectedLibraryRow = _contextRow;
        }
        RefreshSingleGroupMenu();
    }

    private void RefreshSingleGroupMenu()
    {
        if (_vm is null) return;
        var isGroupRow = _contextRow?.IsGroup == true || SelectedLibraryRow?.IsGroup == true;
        SingleJoinGroupMenu.Visibility = isGroupRow ? Visibility.Collapsed : Visibility.Visible;
        SingleNewGroupMenu.Visibility = isGroupRow ? Visibility.Collapsed : Visibility.Visible;
        SingleManageGroupsMenu.Visibility = isGroupRow ? Visibility.Collapsed : Visibility.Visible;
        SingleRemoveGroupMenu.Visibility = isGroupRow ? Visibility.Collapsed : Visibility.Visible;
        SingleCopyFileNameMenu.Visibility = isGroupRow ? Visibility.Collapsed : Visibility.Visible;
        SingleCopyFilePathMenu.Visibility = isGroupRow ? Visibility.Collapsed : Visibility.Visible;
        SingleOpenFilePathMenu.Visibility = isGroupRow ? Visibility.Collapsed : Visibility.Visible;
        if (isGroupRow) return;
        SingleJoinGroupMenu.Items.Clear();
        foreach (var option in _vm.GroupFilterOptions.Skip(1))
        {
            var name = option.Name;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => _vm.AddSelectedToGroup(name);
            SingleJoinGroupMenu.Items.Add(item);
        }
        if (SingleJoinGroupMenu.Items.Count == 0)
            SingleJoinGroupMenu.Items.Add(new MenuItem { Header = "（无分组）", IsEnabled = false });
        SingleRemoveGroupMenu.IsEnabled =
            !string.Equals(_vm.ActiveGroupName, "全部", StringComparison.Ordinal) &&
            _vm.SelectedLibraryRow is { IsGroup: false };
    }

    /// <summary>右键目标行：优先本次右键命中的行，其次当前选中行。</summary>
    private LibraryRow? ContextRow => _contextRow ?? SelectedLibraryRow;

    private void OnCopyBundleId(object sender, RoutedEventArgs e) => CopyToClipboard(LibraryCopy.BundleId(ContextRow), "BundleId");
    private void OnCopyName(object sender, RoutedEventArgs e) => CopyToClipboard(LibraryCopy.Name(ContextRow), "名称");
    private void OnCopyVersion(object sender, RoutedEventArgs e) => CopyToClipboard(LibraryCopy.Version(ContextRow), "版本");
    private void OnCopyMinOs(object sender, RoutedEventArgs e) => CopyToClipboard(LibraryCopy.MinOs(ContextRow), "最低 iOS");
    private void OnCopyFileName(object sender, RoutedEventArgs e) => CopyToClipboard(LibraryCopy.FileName(ContextRow), "文件名");
    private void OnCopyFilePath(object sender, RoutedEventArgs e) => CopyToClipboard(LibraryCopy.FilePath(ContextRow), "文件路径");
    private void OnCopyRow(object sender, RoutedEventArgs e) => CopyToClipboard(LibraryCopy.Row(ContextRow), "整行");

    /// <summary>右键「打开文件路径」：在资源管理器里选中该文件（2026-11-01 用户要求；组行/空路径/文件不存在只提示）。</summary>
    // ── 资源管理器定位（2026-11-02：shell32 SHOpenFolderAndSelectItems，避免启动 explorer.exe 失败） ──
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, [System.Runtime.InteropServices.In] IntPtr[] apidl, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    /// <summary>
    /// 在资源管理器里选中该文件（2026-11-02 修）：必须传 **两级 PIDL**——文件夹 PIDL + 文件相对子 PIDL
    /// （`SHOpenFolderAndSelectItems(folder, 1, [ILFindLastID(file)], 0)`）。只传完整文件 PIDL + 空文件夹会返回
    /// `E_ABORT(0x80004004)`，于是退回 `explorer.exe /select` → 本机报 0xC0000142（踩坑 C32）。
    /// 失败再退 **COM `Shell.Application.Explore`** 打开所在文件夹——不再启动 explorer.exe（避免错误弹窗）。
    /// </summary>
    /// <returns>select=true 已选中文件；select=false 只打开了所在文件夹；false+folderOpened=false 完全失败。</returns>
    private static bool RevealInExplorer(string path, out bool selected, out bool folderOpened)
    {
        selected = false;
        folderOpened = false;
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(folder)) return false;

        IntPtr folderPidl = IntPtr.Zero;
        IntPtr filePidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) == 0 && folderPidl != IntPtr.Zero &&
                SHParseDisplayName(path, IntPtr.Zero, out filePidl, 0, out _) == 0 && filePidl != IntPtr.Zero)
            {
                var child = ILFindLastID(filePidl);
                if (SHOpenFolderAndSelectItems(folderPidl, 1, new[] { child }, 0) == 0)
                {
                    selected = true;
                    folderOpened = true;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // 掉到下面的 COM 兜底
        }
        finally
        {
            if (filePidl != IntPtr.Zero) ILFree(filePidl);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }

        // 兜底：COM 打开所在文件夹（不启动 explorer.exe）
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is not null)
            {
                var shell = Activator.CreateInstance(shellType);
                if (shell is not null)
                {
                    shellType.InvokeMember("Explore", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { folder });
                    folderOpened = true;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // 返回 false，由调用方提示
        }

        return false;
    }

    private void OnOpenFilePath(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var path = LibraryCopy.FilePath(ContextRow);
        if (string.IsNullOrWhiteSpace(path))
        {
            _vm.ShowStatus("没有可打开的文件路径");
            return;
        }

        if (!File.Exists(path))
        {
            _vm.ShowStatus("文件不存在：" + path);
            return;
        }

        try
        {
            var ok = RevealInExplorer(path, out var selected, out var folderOpened);
            if (ok && selected)
                _vm.ShowStatus("已在资源管理器中选中：" + Path.GetFileName(path));
            else if (ok && folderOpened)
                _vm.ShowStatus("已打开所在文件夹：" + Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
            else
                _vm.ShowStatus("未能打开文件路径：" + path);
        }
        catch (Exception ex)
        {
            _vm.ShowStatus("未能打开文件路径：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ── 设备「更多 ▾」菜单（M4-b4：单列「值 + 点击复制」，复制不提示） ─────────────────────

    /// <summary>
    /// 设备页签点击 → 通知 ViewModel 起"停留 800 ms 隐藏终态进度"的计时（2026-11-02 用户定）。
    /// 点已选中的页签不会改 SelectedItem，所以必须在这里直接通知。
    /// </summary>
    private void OnDeviceTabClicked(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;

        var current = e.OriginalSource as DependencyObject;
        while (current is not null and not TabItem) current = VisualTreeHelper.GetParent(current);
        if (current is TabItem { DataContext: DeviceQueueViewModel device }) _vm.NoteDeviceTabClicked(device);
    }

    /// <summary>「更多 ▾」按钮：点一下把按钮的 ContextMenu 弹在它下面。</summary>
    private void OnMoreDeviceInfoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>静默复制（2026-11-01 用户要求：复制不提示）：空值 / 剪贴板被占用都不打扰界面。</summary>
    private static void CopySilently(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (TryCopySilently(text)) return;

        // 实测「启动后第一次复制」偶尔整段被占用（连续 3 次都失败）→ 150 ms 后再补两次（不阻断 UI、不提示）。
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!TryCopySilently(text)) TryCopySilently(text);
        };
        timer.Start();
    }

    /// <summary>立即尝试写剪贴板并读回比对；成功 true（失败不抛、不提示）。</summary>
    private static bool TryCopySilently(string text)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                if (Clipboard.GetText() == text) return true;
            }
            catch (Exception)
            {
                // 剪贴板被其他进程占用等：继续尝试
            }
        }

        return false;
    }

    private void OnCopyDeviceEcidHex(object sender, RoutedEventArgs e) =>
        CopySilently(SelectedDevice?.Device.EcidHex);

    private void OnCopyDeviceEcidDecimal(object sender, RoutedEventArgs e) =>
        CopySilently(SelectedDevice?.Device.Ecid);

    private void OnCopyDeviceSerial(object sender, RoutedEventArgs e) =>
        CopySilently(SelectedDevice?.Device.Serial);

    private void OnCopyDeviceUdid(object sender, RoutedEventArgs e) =>
        CopySilently(SelectedDevice?.Device.Udid);

    private void OnCopyDeviceModelNumber(object sender, RoutedEventArgs e) =>
        CopySilently(SelectedDevice?.Device.ModelNumber);

    private void OnCopyDeviceHardwareModel(object sender, RoutedEventArgs e) =>
        CopySilently(SelectedDevice?.Device.HardwareModel);

    private DeviceQueueViewModel? SelectedDevice => _vm?.SelectedDevice;

    private LibraryRow? SelectedLibraryRow => _vm?.SelectedLibraryRow;

    /// <summary>复制到剪贴板：空白给状态栏提示；剪贴板被占用等异常不崩（只提示）。</summary>
    private void CopyToClipboard(string? text, string what)
    {
        if (_vm is null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            _vm.ShowStatus($"没有可复制的{what}");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _vm.ShowStatus($"已复制：{what}");
        }
        catch (Exception ex)
        {
            _vm.ShowStatus($"未能复制{what}：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 导出日志（2026-11-01 用户要求）：默认目录**程序目录**、默认名 `日志-yyyyMMdd-HHmmss.txt`；
    /// 对话框是用户交互（UI 线程），真正写盘在 <see cref="MainViewModel.ExportLogAsync"/> 里走 Task.Run。
    /// </summary>
    /// <summary>B4 收尾（2026-11-04）：只清空日志面板，不动文件日志。</summary>
    private void OnClearLogClick(object sender, RoutedEventArgs e) => _vm?.ClearLog();

    /// <summary>
    /// 列显示/隐藏接口（M4-b5，2026-11-04）：按稳定 key 改运行时列可见性；
    /// 关窗时 <see cref="CaptureLibraryColumns"/> 会把当前可见性一起写进 `Grid.Columns`。
    /// </summary>
    internal void SetLibraryColumnVisible(string key, bool visible)
    {
        foreach (var column in LibraryGrid.Columns)
        {
            if (_columnKeys.TryGetValue(column, out var columnKey) && string.Equals(columnKey, key, StringComparison.Ordinal))
            {
                column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
        }
    }

    /// <summary>读运行时列可见性；key 不存在返回 null。</summary>
    internal bool? IsLibraryColumnVisible(string key)
    {
        foreach (var column in LibraryGrid.Columns)
        {
            if (_columnKeys.TryGetValue(column, out var columnKey) && string.Equals(columnKey, key, StringComparison.Ordinal))
                return column.Visibility == Visibility.Visible;
        }

        return null;
    }

    private void OnAdvancedFilterClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var window = new AdvancedFilterWindow(_vm.CaptureAdvancedFilter(), state => _vm.ApplyAdvancedFilter(state), this);
        window.ShowDialog();
    }

    private async void OnExportLogClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出日志",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
            FileName = LogExport.DefaultFileName(DateTime.Now),
            InitialDirectory = AppPaths.ProgramDirectory,
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true) return;
        try { await _vm.ExportLogAsync(dialog.FileName); }
        catch (Exception ex) { SafeFireAndForget.Report("导出日志", ex); }
    }

    // ── 搜索范围 / 匹配方式（2026-11-01 用户定） ─────────────────────────
    // ── 添加…（目录 / 文件，2026-11-02 用户定） ───────────────────────────
    private void OnAddMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private async void OnAddRootClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要加入扫描根的目录",
                Multiselect = false,
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            await _vm.AddRootAsync(dialog.FolderName);
        }
        catch (Exception ex)
        {
            _vm.ShowStatus("未能添加扫描根：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ── 刷新：清除状态标记（按钮 + 右键菜单共用） ─────────────────────────
    /// <summary>
    /// 刷新（工具栏 ⟳ + 右键菜单共用；2026-11-02 用户定**与「清空已结束」合并**）：
    /// 清当前设备已结束任务 + 清资料库红/黄/绿标记（勾保留）。
    /// </summary>
    private void OnClearMarksClick(object sender, RoutedEventArgs e) => _vm?.RefreshMarksAndClearFinished();

    // ── 多选 / 安装到…（2026-11-07） ──────────────────────────────────────────

    private void OnToggleMultiSelectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ToggleMultiSelectMode();
        ApplyMultiSelectMode();
    }

    private void ApplyMultiSelectMode()
    {
        if (_vm is null) return;
        if (_vm.IsMultiSelectMode)
        {
            LibraryGrid.SelectedItem = null;
            ClearArmedRow();
            _multiLibraryContextMenu = BuildInstallToMenu(includeSelectionActions: true);
            LibraryGrid.ContextMenu = _multiLibraryContextMenu;
        }
        else
        {
            LibraryGrid.ContextMenu = _singleLibraryContextMenu;
        }
    }

    private void OnInstallToMenuClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = BuildInstallToMenu(includeSelectionActions: false);
        menu.PlacementTarget = InstallToButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>顶部「安装到…」/ 多选右键共用菜单；具体设备二级悬浮只有「安装」。</summary>
    private ContextMenu BuildInstallToMenu(bool includeSelectionActions, bool isGroupRow = false)
    {
        var menu = new ContextMenu();

        var all = new MenuItem { Header = "全部设备" };
        var allInstall = new MenuItem { Header = "安装" };
        allInstall.Click += (_, _) => _vm?.EnqueueSelectionToAllOnline();
        all.Items.Add(allInstall);
        menu.Items.Add(all);

        var more = new MenuItem { Header = "更多…" };
        more.Click += (_, _) => OpenInstallToWindow();
        menu.Items.Add(more);
        menu.Items.Add(new Separator());

        if (_vm is not null)
        {
            foreach (var device in _vm.Devices.Where(d => d.Device.Online))
            {
                var target = device;
                var deviceItem = new MenuItem { Header = target.Device.ShortName };
                var install = new MenuItem { Header = "安装" };
                install.Click += (_, _) => _vm?.EnqueueSelectionToDevice(target);
                deviceItem.Items.Add(install);
                menu.Items.Add(deviceItem);
            }
        }

        if (includeSelectionActions)
        {
            if (!isGroupRow)
            {
                menu.Items.Add(new Separator());
                var join = new MenuItem { Header = "加入分组" };
            if (_vm is not null)
            {
                foreach (var groupOption in _vm.GroupFilterOptions.Skip(1))
                {
                    var name = groupOption.Name;
                    var item = new MenuItem { Header = name };
                    item.Click += (_, _) => _vm?.AddSelectedToGroup(name);
                    join.Items.Add(item);
                }
            }
            if (join.Items.Count == 0)
                join.Items.Add(new MenuItem { Header = "（无分组）", IsEnabled = false });
            menu.Items.Add(join);
            menu.Items.Add(NewMenuItem("新建分组…", OnNewGroupClick));
            menu.Items.Add(NewMenuItem("管理分组…", OnManageGroupsClick));
            var remove = new MenuItem
            {
                Header = "移出当前分组",
                IsEnabled = _vm is not null && !string.Equals(_vm.ActiveGroupName, "全部", StringComparison.Ordinal),
            };
                remove.Click += (_, _) => _vm?.RemoveSelectedFromActiveGroup();
                menu.Items.Add(remove);
            }

            menu.Items.Add(new Separator());
            var selectAll = new MenuItem { Header = "全部选中" };
            selectAll.Click += (_, _) => _vm?.SelectAllVisibleLibraryRows();
            menu.Items.Add(selectAll);
            var clear = new MenuItem { Header = "全部取消选中" };
            clear.Click += (_, _) => _vm?.ClearMultiSelection();
            menu.Items.Add(clear);
        }

        return menu;
    }

    private static MenuItem NewMenuItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dialog = new GroupNameWindow("新建分组", "") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var error = _vm.CreateGroup(dialog.GroupName);
        if (error is not null)
            MessageBox.Show(this, error, "新建分组", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnRemoveFromGroupClick(object sender, RoutedEventArgs e)
        => _vm?.RemoveSelectedFromActiveGroup();

    private void OnManageGroupsClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        new GroupManagerWindow(_vm) { Owner = this }.ShowDialog();
    }

    private void OpenInstallToWindow()
    {
        if (_vm is null) return;
        var window = new InstallToWindow(_vm) { Owner = this };
        if (window.ShowDialog() == true)
            _vm.EnqueueSelectionToUdids(window.SelectedUdids);
    }

    private async void OnDeleteMissingRecordsClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.DeleteMissingRecordsAsync();
    }

    private void OnToggleMissingClick(object sender, RoutedEventArgs e) => _vm?.ToggleMissingFiles();

    private void OnSearchOptionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;

        // 菜单里的勾选状态每次打开时按 VM 同步（模式项不走绑定，避免双向绑定打架）
        ModeContains.IsChecked = _vm.SearchMatchMode == SearchMatchMode.Contains;
        ModePrefix.IsChecked = _vm.SearchMatchMode == SearchMatchMode.Prefix;
        ModeExact.IsChecked = _vm.SearchMatchMode == SearchMatchMode.Exact;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void OnSearchModeContains(object sender, RoutedEventArgs e) => SetSearchMode(SearchMatchMode.Contains);

    private void OnSearchModePrefix(object sender, RoutedEventArgs e) => SetSearchMode(SearchMatchMode.Prefix);

    private void OnSearchModeExact(object sender, RoutedEventArgs e) => SetSearchMode(SearchMatchMode.Exact);

    private void SetSearchMode(SearchMatchMode mode)
    {
        _vm.SetSearchMatchMode(mode);
        ModeContains.IsChecked = mode == SearchMatchMode.Contains;
        ModePrefix.IsChecked = mode == SearchMatchMode.Prefix;
        ModeExact.IsChecked = mode == SearchMatchMode.Exact;

        if (SearchOptionsButton.ContextMenu is { } menu) menu.IsOpen = false;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null) return;      // InitializeComponent 期间可能先触发
        // 防抖在 ViewModel 里做（b3b：>1000 行才延迟 300 ms，日常即时；可单测）
        _vm.LibrarySearchText = SearchBox.Text;
    }

    private void OnMergeToggled(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.LibraryMergeSameBundle = MergeCheck.IsChecked == true;
    }

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsMultiSelectMode)
        {
            // 多选走自定义 IsMultiSelected，不让 DataGrid 的灰色系统选中态盖上来。
            if (LibraryGrid.SelectedItem is not null) LibraryGrid.SelectedItem = null;
            return;
        }
        _vm.SelectedLibraryRow = LibraryGrid.SelectedItem as LibraryRow;
    }

    /// <summary>点列头：把排序交给 <see cref="LibraryView"/>（组行跟着重排，不会散架）。</summary>
    private void OnLibrarySorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_vm is null) return;
        if (_columnKeys.TryGetValue(e.Column, out var key) is false) return;
        if (!Enum.TryParse<LibrarySortField>(ToSortField(key), out var field)) return;

        // 2026-11-01 用户定：第一次点该列按列默认方向（数值列降序 / 文本列升序），同列再点则反向
        var descending = LibraryPrefs.NextDescending(_vm.LibrarySort, field);

        // 审查 N1：先真正排序（写 _vm.LibrarySort），再同步 ▲/▼，否则箭头读的是旧方向
        _vm.SetLibrarySort(field, descending);
        ApplySortIndicators(e.Column);
    }

    /// <summary>把 ▲/▼ 指示器同步到指定列（其余列清空）。</summary>
    private void ApplySortIndicators(DataGridColumn? sortedColumn)
    {
        if (_vm is null) return;

        var sort = _vm.LibrarySort;
        foreach (var column in LibraryGrid.Columns)
        {
            if (!_columnKeys.TryGetValue(column, out var key)) continue;
            if (!Enum.TryParse<LibrarySortField>(ToSortField(key), out var field)) continue;

            if (sortedColumn is not null && !ReferenceEquals(column, sortedColumn)) continue;

            column.SortDirection = field == sort.Field
                ? (sort.Descending
                    ? System.ComponentModel.ListSortDirection.Descending
                    : System.ComponentModel.ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>启动/重建后按当前排序刷新列头箭头。</summary>
    private void SyncSortIndicatorsFromViewModel()
    {
        if (_vm is null) return;
        ApplySortIndicators(sortedColumn: null);
    }

    private static string ToSortField(string key) => key switch
    {
        LibraryColumns.AppSize => nameof(LibrarySortField.AppSize),
        LibraryColumns.Name => nameof(LibrarySortField.Name),
        LibraryColumns.Version => nameof(LibrarySortField.Version),
        LibraryColumns.MinOs => nameof(LibrarySortField.MinOs),
        LibraryColumns.DeviceFamily => nameof(LibrarySortField.DeviceFamily),
        LibraryColumns.BundleId => nameof(LibrarySortField.BundleId),
        LibraryColumns.Arch => nameof(LibrarySortField.Arch),
        LibraryColumns.FileSize => nameof(LibrarySortField.FileSize),
        LibraryColumns.State => nameof(LibrarySortField.State),
        _ => "",
    };

    // ── 行高快捷调节（2026-11-01 用户定：设置键 Grid.RowHeight；设置面板留 M7） ──────────
    private void OnRowHeightSmall(object sender, RoutedEventArgs e) => SetLibraryRowHeight(24);

    private void OnRowHeightMedium(object sender, RoutedEventArgs e) => SetLibraryRowHeight(32);

    private void OnRowHeightLarge(object sender, RoutedEventArgs e) => SetLibraryRowHeight(40);

    private void SetLibraryRowHeight(double value)
    {
        if (_vm is null) return;
        _vm.LibraryRowHeight = value;
        ApplyIconColumnWidth();
    }

    // ── 左侧「资料库 / 设备应用」切换（2026-11-06 最小原型） ────────────────

    private void OnLibraryModeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.IsDeviceAppsMode) return;
        _vm.IsDeviceAppsMode = false;
        ApplyLeftMode();
    }

    private void OnDeviceAppsModeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.IsDeviceAppsMode) return;
        _vm.IsDeviceAppsMode = true;
        ApplyLeftMode();
    }

    private void ApplyLeftMode()
    {
        var deviceMode = _vm?.IsDeviceAppsMode == true;
        LibraryModeButton.FontWeight = deviceMode ? FontWeights.Normal : FontWeights.Bold;
        DeviceAppsModeButton.FontWeight = deviceMode ? FontWeights.Bold : FontWeights.Normal;
        var libraryControls = deviceMode ? Visibility.Collapsed : Visibility.Visible;
        IncrementalScanButton.Visibility = libraryControls;
        RescanAllButton.Visibility = libraryControls;
        MergeCheck.Visibility = libraryControls;
        LibrarySearchBorder.Visibility = libraryControls;
        AdvancedFilterToggle.Visibility = libraryControls;
        AdvancedFilterButton.Visibility = libraryControls;
        CompatFilterCombo.Visibility = libraryControls;
        LibraryCount.Visibility = libraryControls;
        LibraryGrid.Visibility = deviceMode ? Visibility.Collapsed : Visibility.Visible;
        DeviceAppsGrid.Visibility = deviceMode ? Visibility.Visible : Visibility.Collapsed;
        if (!deviceMode) ClearDeviceAppArmed();
    }

    private DeviceAppRow? _deviceAppArmed;
    private long _deviceAppArmedAtMs;
    private DispatcherTimer? _deviceAppArmedTimer;

    private void OnDeviceAppsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        var row = FindDataContext<DeviceAppRow>(e.OriginalSource);
        if (row is null) return;

        var now = Environment.TickCount64;
        if (ReferenceEquals(row, _deviceAppArmed) && DoubleClickState.IsSecondClick(_deviceAppArmedAtMs, now))
        {
            ClearDeviceAppArmed();
            _vm.UninstallDeviceApp(row);
            e.Handled = true;
            return;
        }

        // 复用资料库的单击待确认视觉：第一次单击深蓝，400 ms 后恢复（选中行显示灰）
        ClearDeviceAppArmed();
        _deviceAppArmed = row;
        _deviceAppArmedAtMs = now;
        row.IsArmed = true;
        _deviceAppArmedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DoubleClickState.WindowMs) };
        _deviceAppArmedTimer.Tick += (_, _) => ClearDeviceAppArmed();
        _deviceAppArmedTimer.Start();
    }

    private void ClearDeviceAppArmed()
    {
        if (_deviceAppArmedTimer is not null)
        {
            _deviceAppArmedTimer.Stop();
            _deviceAppArmedTimer = null;
        }

        if (_deviceAppArmed is not null) _deviceAppArmed.IsArmed = false;
        _deviceAppArmed = null;
    }

    private static T? FindDataContext<T>(object? source) where T : class
    {
        var current = source as DependencyObject;
        while (current is not null and not DataGridRow)
            current = VisualTreeHelper.GetParent(current);
        return (current as DataGridRow)?.Item as T;
    }

    // ── 资料库行交互（2026-11-01 用户定：单击组行展开；同列两次按下 ≤400 ms = 双击入队/取消） ──

    private LibraryRow? _armedRow;
    private DataGridColumn? _armedColumn;
    private long _armedAtMs;
    private DispatcherTimer? _armedTimer;

    private void OnLibraryPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;

        var row = FindRowFromSource(e.OriginalSource);
        if (row is null) return;
        if (row.IsAppendedSeparator) { e.Handled = true; return; }

        // 组行：单击展开 / 收起（双击不再承担这个动作，避免和双击入队冲突）
        if (row.IsGroup)
        {
            _vm.ToggleLibraryGroup(row);
            e.Handled = true;
            return;
        }

        // 多选模式：单击只切换选中；按下后移动超过阈值则改为拖动范围选择（Explorer 风格）。
        if (_vm.IsMultiSelectMode)
        {
            ClearArmedRow();
            _dragCandidateRow = row;
            _dragStartPoint = e.GetPosition(LibraryGrid);
            _dragMoved = false;
            LibraryGrid.CaptureMouse();
            e.Handled = true;
            return;
        }

        var column = FindColumnFromSource(e.OriginalSource);

        var now = Environment.TickCount64;
        if (ReferenceEquals(row, _armedRow) && ReferenceEquals(column, _armedColumn) &&
            DoubleClickState.IsSecondClick(_armedAtMs, now))
        {
            ClearArmedRow();
            _ = SafeFireAndForget.Run(() => _vm.ToggleLibraryQueueAsync(row), "资料库双击入队");
            e.Handled = true;
            return;
        }

        ArmRow(row, column, now);
    }

    private void OnLibraryPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidateRow is null || e.LeftButton != MouseButtonState.Pressed || _vm is null) return;
        var pos = e.GetPosition(LibraryGrid);
        if (!_dragMoved)
        {
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!_vm.BeginDragSelection(_dragCandidateRow))
            {
                _dragCandidateRow = null;
                LibraryGrid.ReleaseMouseCapture();
                return;
            }
            _dragMoved = true;
        }

        var current = FindRowAtPoint(pos)?.Item as LibraryRow;
        if (current is not null && !current.IsGroup && !current.IsAppendedSeparator)
            _vm.SelectDragRange(_dragCandidateRow, current);
        e.Handled = true;
    }

    private void OnLibraryPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var candidate = _dragCandidateRow;
        var moved = _dragMoved;
        _dragCandidateRow = null;
        _dragMoved = false;
        if (LibraryGrid.IsMouseCaptured) LibraryGrid.ReleaseMouseCapture();
        if (candidate is null || _vm is null) return;
        if (!moved) _vm.ToggleLibraryMultiSelect(candidate);
        e.Handled = true;
    }

    private DataGridRow? FindRowAtPoint(System.Windows.Point point)
    {
        var hit = LibraryGrid.InputHitTest(point) as DependencyObject;
        return FindAncestor<DataGridRow>(hit);
    }

    private void ArmRow(LibraryRow row, DataGridColumn? column, long now)
    {
        ClearArmedRow();
        _armedRow = row;
        _armedColumn = column;
        _armedAtMs = now;
        row.IsArmed = true;

        _armedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DoubleClickState.WindowMs) };
        _armedTimer.Tick += (_, _) => ClearArmedRow();
        _armedTimer.Start();
    }

    private void ClearArmedRow()
    {
        if (_armedTimer is not null)
        {
            _armedTimer.Stop();
            _armedTimer = null;
        }

        if (_armedRow is not null) _armedRow.IsArmed = false;
        _armedRow = null;
        _armedColumn = null;
    }

    /// <summary>取双击位置所在的列（同一行不同列不算双击，审查 N5）。</summary>
    private static DataGridColumn? FindColumnFromSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null and not DataGridCell)
            current = VisualTreeHelper.GetParent(current);

        return (current as DataGridCell)?.Column;
    }

    // ── 队列行双击取消（2026-11-01 用户定；复用 DoubleClickState + MainViewModel.CancelJob） ──
    private JobViewModel? _queueArmedJob;
    private long _queueArmedAtMs;

    private void OnQueuePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.SelectedDevice is not { } device) return;

        // 命中 ListBoxItem 的 DataContext（JobViewModel）
        var current = e.OriginalSource as DependencyObject;
        while (current is not null and not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        if ((current as ListBoxItem)?.DataContext is not JobViewModel job) return;

        var now = Environment.TickCount64;
        if (ReferenceEquals(job, _queueArmedJob) && DoubleClickState.IsSecondClick(_queueArmedAtMs, now))
        {
            _queueArmedJob = null;
            _vm.HandleQueueItemDoubleClick(device, job);
            e.Handled = true;
            return;
        }

        _queueArmedJob = job;
        _queueArmedAtMs = now;
    }

    private static LibraryRow? FindRowFromSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null and not DataGridRow)
            current = VisualTreeHelper.GetParent(current);

        return (current as DataGridRow)?.Item as LibraryRow;
    }

    /// <summary>按当前列顺序 / 宽度/ 显隐抓一份配置（关窗时写库）。</summary>
    internal IReadOnlyList<GridColumnState> CaptureLibraryColumns()
    {
        var entries = new List<GridColumnEntry>(LibraryGrid.Columns.Count);
        foreach (var column in LibraryGrid.Columns)
        {
            if (!_columnKeys.TryGetValue(column, out var key)) continue;
            var width = double.IsNaN(column.ActualWidth) || column.ActualWidth <= 0
                ? column.Width.DisplayValue
                : column.ActualWidth;
            // 顺序按 DisplayIndex 抓：WPF 拖动换序只改 DisplayIndex，不动 Columns 集合（审核 P2-4）
            entries.Add(new GridColumnEntry(key, column.DisplayIndex, Math.Round(width, 1),
                column.Visibility == Visibility.Visible));
        }

        return LibraryColumns.OrderForSave(entries);
    }

    // ── UI 卡顿看门狗（临时诊断：env IDEVICEGUI_UIWATCH=1 才开，写 logs\ui-stall.log）──

    private System.Diagnostics.Stopwatch? _uiWatchClock;
    private long _uiWatchLast;

    /// <summary>
    /// 每 50 ms 打一次点；两次打点间隔 &gt; 300 ms 就记一行——用来判断"卡住"是发生在 UI 线程内，
    /// 还是 UIA 客户端在等（M4-b2 排查"首次滚动每步 1–2 s"用；默认关闭）。
    /// </summary>
    private void StartUiWatchdog()
    {
        if (Environment.GetEnvironmentVariable("IDEVICEGUI_UIWATCH") != "1") return;

        _uiWatchClock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var now = _uiWatchClock!.ElapsedMilliseconds;
            var gap = now - _uiWatchLast;
            _uiWatchLast = now;
            if (gap <= 300) return;
            try
            {
                var path = Path.Combine(AppPaths.LogsDirectory, "ui-stall.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} stall {gap} ms\n");
            }
            catch
            {
                // 诊断日志写不了就算了
            }
        };
        timer.Start();
    }

    private string[]? PickIpaFiles()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "iOS 安装包 (*.ipa)|*.ipa|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
    }

    // ── 设置窗口（2026-11-02 用户定；窗口本体在 SettingsWindow）─────────────────

    /// <summary>当前是否隐藏日志栏（设置窗口读）。</summary>
    internal bool SettingsLogHidden => _logHidden;

    /// <summary>当前日志浮层宽度（设置窗口读）。</summary>
    internal double SettingsLogOverlayWidth => _layout.LogOverlayWidth;

    /// <summary>
    /// 设置窗口的布局预览/应用：日志栏显隐 + 浮层宽度（三栏比例只由"恢复默认"按钮动）。
    /// </summary>
    internal void ApplySettingsLayout(bool logHidden, double overlayWidth)
    {
        if (_logHidden != logHidden) ToggleLog();
        _layout = _layout with { LogOverlayWidth = Math.Clamp(overlayWidth, 260, 900) };
    }

    /// <summary>设置窗口「恢复默认三栏比例」。</summary>
    internal void ResetSettingsLayoutColumns()
    {
        _layout = _layout with { Columns = LayoutState.Default.Columns };
        ApplyLayout(_layout);
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (!_vm.TryBeginOpenSettings()) return;

        try
        {
            var window = new SettingsWindow(_vm, this) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            // 设置窗口打不开也不能把程序带崩：写 crash.log 留证据
            Services.CrashReporter.Write("未能打开设置窗口", ex);
            MessageBox.Show(this, "未能打开设置窗口：" + ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _vm.EndOpenSettings();
        }
    }
}
