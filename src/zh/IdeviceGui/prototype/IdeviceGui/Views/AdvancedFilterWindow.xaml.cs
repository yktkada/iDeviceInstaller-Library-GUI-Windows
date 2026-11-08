using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdeviceGui.ViewModels.Library;

namespace IdeviceGui.Views;

/// <summary>
/// 高级筛选窗口（2026-11-05）：组内 AND/OR、组间 AND、同一列不能重复；
/// 条件持久化由 MainViewModel 负责，本窗口只在「应用」时回传草稿。
/// </summary>
internal partial class AdvancedFilterWindow : Window
{
    private readonly Action<AdvancedFilterState> _onApply;
    private readonly AdvancedFilterState _working;
    private readonly List<GroupUi> _groups = new();

    private static readonly (AdvancedFilterColumn Column, string Label)[] ColumnOptions =
    {
        (AdvancedFilterColumn.Name, "名称"),
        (AdvancedFilterColumn.BundleId, "BundleId"),
        (AdvancedFilterColumn.FilePath, "文件路径"),
        (AdvancedFilterColumn.Version, "版本"),
        (AdvancedFilterColumn.MinOs, "最低 iOS"),
        (AdvancedFilterColumn.AppSizeMb, "解压后体积"),
        (AdvancedFilterColumn.FileSizeMb, "原始大小"),
        (AdvancedFilterColumn.DeviceFamily, "设备族"),
        (AdvancedFilterColumn.Origin, "来源"),
        (AdvancedFilterColumn.Arch, "架构"),
        (AdvancedFilterColumn.State, "状态"),
        (AdvancedFilterColumn.Compatibility, "兼容性"),
    };

    private static readonly string[] PhoneSides = { "", "4.7\"/5.5\"", "4\"", "3.5\"", "OG 3.5\"" };
    private static readonly string[] PadSides = { "", "iPad Retina", "iPad" };
    private static readonly string[] ArchOptions = { "v6", "v7", "v7f", "v7s", "arm64", "多种", "—" };
    private static readonly string[] OriginOptions = { "原始 App Store", "已重签名", "已解密" };
    private static readonly string[] StateOptions = { "正常", "文件缺失", "解析未完成", "待解析" };

    public AdvancedFilterWindow(AdvancedFilterState state, Action<AdvancedFilterState> onApply, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        _onApply = onApply;
        _working = state.Clone();
        if (_working.Groups.Count == 0) _working.Groups.Add(new AdvancedFilterGroup());
        foreach (var group in _working.Groups) AddGroupUi(group);
    }

    private void AddGroupUi(AdvancedFilterGroup group)
    {
        var box = new GroupBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = $"组 {_groups.Count + 1}", VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = "  组内：", VerticalAlignment = VerticalAlignment.Center });
        var join = new ComboBox { Width = 70, ItemsSource = new[] { "且", "或" }, SelectedIndex = group.Join == FilterJoin.And ? 0 : 1 };
        header.Children.Add(join);
        var add = new Button { Content = "＋条件", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(12, 0, 0, 0) };
        header.Children.Add(add);
        if (_groups.Count > 0)
        {
            var remove = new Button { Content = "删除组", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
            header.Children.Add(remove);
            remove.Click += (_, _) => RemoveGroup(box);
        }

        box.Header = header;
        var rows = new StackPanel();
        box.Content = rows;
        GroupsPanel.Children.Add(box);
        var ui = new GroupUi(box, join, rows);
        _groups.Add(ui);
        add.Click += (_, _) => AddConditionUi(ui, new AdvancedFilterCondition());
        foreach (var condition in group.Conditions) AddConditionUi(ui, condition);
    }

    private void AddConditionUi(GroupUi group, AdvancedFilterCondition condition)
    {
        var row = new ConditionUi(group, condition);
        group.Rows.Add(row);
        group.RowsPanel.Children.Add(row.Root);
    }

    private void RemoveGroup(GroupBox box)
    {
        var index = _groups.FindIndex(g => ReferenceEquals(g.Box, box));
        if (index < 0) return;
        _groups.RemoveAt(index);
        GroupsPanel.Children.Remove(box);
        // 重新编号
        for (var i = 0; i < _groups.Count; i++)
            if (_groups[i].Box.Header is StackPanel header && header.Children[0] is TextBlock label)
                label.Text = $"组 {i + 1}";
    }

    private void OnAddGroupClick(object sender, RoutedEventArgs e) =>
        AddGroupUi(new AdvancedFilterGroup());

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        _groups.Clear();
        GroupsPanel.Children.Clear();
        AddGroupUi(new AdvancedFilterGroup());
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var state = CollectState();
        var duplicate = state.Groups
            .SelectMany(g => g.Conditions)
            .GroupBy(c => c.Column)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            MessageBox.Show(this, $"列不能重复选中：{LabelOf(duplicate.Key)}", "高级筛选",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _onApply(state);
        Close();
    }

    private AdvancedFilterState CollectState()
    {
        var state = new AdvancedFilterState { Enabled = _working.Enabled, Version = _working.Version };
        foreach (var group in _groups)
        {
            var filterGroup = new AdvancedFilterGroup
            {
                Join = group.Join.SelectedIndex == 1 ? FilterJoin.Or : FilterJoin.And,
            };
            foreach (var row in group.Rows)
            {
                var condition = row.Collect();
                if (!condition.IsBlank) filterGroup.Conditions.Add(condition);
            }
            if (filterGroup.Conditions.Count > 0) state.Groups.Add(filterGroup);
        }

        return state;
    }

    private static string LabelOf(AdvancedFilterColumn column) =>
        ColumnOptions.First(o => o.Column == column).Label;

    private sealed class GroupUi
    {
        public GroupUi(GroupBox box, ComboBox join, StackPanel rowsPanel)
        {
            Box = box;
            Join = join;
            RowsPanel = rowsPanel;
        }

        public GroupBox Box { get; }
        public ComboBox Join { get; }
        public StackPanel RowsPanel { get; }
        public List<ConditionUi> Rows { get; } = new();
    }

    /// <summary>一行条件：启用勾选 + 列选择 + 动态编辑器。</summary>
    private sealed class ConditionUi
    {
        private readonly AdvancedFilterCondition _source;
        private readonly ComboBox _column;
        private readonly Border _editorHost;
        private readonly CheckBox _enabled;
        private readonly GroupUi _group;

        private ComboBox? _textMode;
        private TextBox? _text;
        private ComboBox? _versionMode;
        private TextBox? _versionExact;
        private StackPanel? _rangePanel;
        private TextBox? _min;
        private TextBox? _max;
        private ComboBox? _phone;
        private ComboBox? _pad;
        private TextBlock? _familyPreview;
        private readonly List<CheckBox> _multi = new();
        private ComboBox? _compat;

        public ConditionUi(GroupUi group, AdvancedFilterCondition condition)
        {
            _group = group;
            _source = condition;
            _enabled = new CheckBox { IsChecked = condition.Enabled, VerticalAlignment = VerticalAlignment.Center };
            _enabled.Checked += (_, _) => ApplyEnabled();
            _enabled.Unchecked += (_, _) => ApplyEnabled();

            _column = new ComboBox
            {
                Width = 150,
                ItemsSource = ColumnOptions.Select(o => o.Label).ToArray(),
                SelectedIndex = Math.Max(0, Array.FindIndex(ColumnOptions, o => o.Column == condition.Column)),
                Margin = new Thickness(6, 0, 6, 0),
            };
            _column.SelectionChanged += (_, _) => BuildEditor();

            var remove = new Button { Content = "×", Width = 24, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) => Remove();

            _editorHost = new Border { Padding = new Thickness(2) };
            var root = new StackPanel { Orientation = Orientation.Horizontal };
            root.Children.Add(_enabled);
            root.Children.Add(_column);
            root.Children.Add(_editorHost);
            root.Children.Add(remove);

            Root = new Border
            {
                Child = root,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
            };
            BuildEditor();
            ApplyEnabled();
        }

        public Border Root { get; }

        private AdvancedFilterColumn CurrentColumn =>
            ColumnOptions[Math.Max(0, _column.SelectedIndex)].Column;

        private void ApplyEnabled()
        {
            Root.Opacity = _enabled.IsChecked == true ? 1.0 : 0.45;
            _column.IsEnabled = _enabled.IsChecked == true;
            _editorHost.IsEnabled = _enabled.IsChecked == true;
        }

        private void Remove()
        {
            _group.Rows.Remove(this);
            _group.RowsPanel.Children.Remove(Root);
        }

        private void BuildEditor()
        {
            _editorHost.Child = null;
            _textMode = null;
            _text = null;
            _versionMode = null;
            _versionExact = null;
            _rangePanel = null;
            _min = null;
            _max = null;
            _phone = null;
            _pad = null;
            _familyPreview = null;
            _multi.Clear();
            _compat = null;

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var column = CurrentColumn;
            switch (column)
            {
                case AdvancedFilterColumn.Name:
                case AdvancedFilterColumn.BundleId:
                case AdvancedFilterColumn.FilePath:
                {
                    _textMode = new ComboBox { Width = 70, ItemsSource = new[] { "精确", "包含" }, SelectedIndex = _source.TextMode == TextFilterMode.Contains ? 1 : 0 };
                    _text = new TextBox { Width = 260, Text = _source.Text, Margin = new Thickness(6, 0, 0, 0) };
                    panel.Children.Add(_textMode);
                    panel.Children.Add(_text);
                    break;
                }
                case AdvancedFilterColumn.Version:
                case AdvancedFilterColumn.MinOs:
                {
                    _versionMode = new ComboBox { Width = 70, ItemsSource = new[] { "精确", "范围" }, SelectedIndex = (string.IsNullOrWhiteSpace(_source.Min) && string.IsNullOrWhiteSpace(_source.Max)) ? 0 : 1 };
                    _versionExact = new TextBox { Width = 120, Text = _source.Text, Margin = new Thickness(6, 0, 0, 0) };
                    _rangePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
                    _min = new TextBox { Width = 80, Text = _source.Min };
                    _max = new TextBox { Width = 80, Text = _source.Max, Margin = new Thickness(6, 0, 0, 0) };
                    _rangePanel.Children.Add(_min);
                    _rangePanel.Children.Add(new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center });
                    _rangePanel.Children.Add(_max);
                    _versionMode.SelectionChanged += (_, _) => ToggleVersionMode();
                    panel.Children.Add(_versionMode);
                    panel.Children.Add(_versionExact);
                    panel.Children.Add(_rangePanel);
                    ToggleVersionMode();
                    break;
                }
                case AdvancedFilterColumn.AppSizeMb:
                case AdvancedFilterColumn.FileSizeMb:
                {
                    _min = new TextBox { Width = 90, Text = _source.Min };
                    _max = new TextBox { Width = 90, Text = _source.Max, Margin = new Thickness(6, 0, 0, 0) };
                    panel.Children.Add(new TextBlock { Text = "MB ", VerticalAlignment = VerticalAlignment.Center });
                    panel.Children.Add(_min);
                    panel.Children.Add(new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center });
                    panel.Children.Add(_max);
                    panel.Children.Add(new TextBlock { Text = " MB", VerticalAlignment = VerticalAlignment.Center });
                    break;
                }
                case AdvancedFilterColumn.DeviceFamily:
                {
                    _phone = new ComboBox { Width = 120, ItemsSource = PhoneSides, SelectedItem = _source.PhoneSide ?? "" };
                    _pad = new ComboBox { Width = 120, ItemsSource = PadSides, SelectedItem = _source.PadSide ?? "", Margin = new Thickness(6, 0, 0, 0) };
                    _familyPreview = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
                    _phone.SelectionChanged += (_, _) => UpdateFamilyPreview();
                    _pad.SelectionChanged += (_, _) => UpdateFamilyPreview();
                    panel.Children.Add(new TextBlock { Text = "iPhone 侧 ", VerticalAlignment = VerticalAlignment.Center });
                    panel.Children.Add(_phone);
                    panel.Children.Add(new TextBlock { Text = " iPad 侧 ", VerticalAlignment = VerticalAlignment.Center });
                    panel.Children.Add(_pad);
                    panel.Children.Add(_familyPreview);
                    UpdateFamilyPreview();
                    break;
                }
                case AdvancedFilterColumn.Arch:
                case AdvancedFilterColumn.State:
                case AdvancedFilterColumn.Origin:
                {
                    var options = column switch
                    {
                        AdvancedFilterColumn.Arch => ArchOptions,
                        AdvancedFilterColumn.Origin => OriginOptions,
                        _ => StateOptions,
                    };
                    var wrap = new WrapPanel();
                    foreach (var option in options)
                    {
                        var check = new CheckBox
                        {
                            Content = option,
                            Margin = new Thickness(0, 0, 8, 0),
                            IsChecked = _source.Selected.Contains(option, StringComparer.Ordinal),
                        };
                        _multi.Add(check);
                        wrap.Children.Add(check);
                    }
                    panel.Children.Add(wrap);
                    break;
                }
                case AdvancedFilterColumn.Compatibility:
                {
                    _compat = new ComboBox
                    {
                        Width = 140,
                        ItemsSource = new[] { "不过滤", "可安装到该设备", "已适配到该设备" },
                        SelectedIndex = _source.Compatibility switch
                        {
                            CompatFilterMode.Adapted => 2,
                            CompatFilterMode.Installable => 1,
                            _ => 0,
                        },
                    };
                    panel.Children.Add(_compat);
                    break;
                }
            }

            _editorHost.Child = panel;
        }

        private void ToggleVersionMode()
        {
            if (_versionMode is null || _versionExact is null || _rangePanel is null) return;
            var isRange = _versionMode.SelectedIndex == 1;
            _versionExact.Visibility = isRange ? Visibility.Collapsed : Visibility.Visible;
            _rangePanel.Visibility = isRange ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFamilyPreview()
        {
            if (_familyPreview is null) return;
            var phone = _phone?.SelectedItem as string ?? "";
            var pad = _pad?.SelectedItem as string ?? "";
            var text = phone.Length > 0 && pad.Length > 0 ? phone + " + " + pad
                : phone.Length > 0 ? phone
                : pad;
            _familyPreview.Text = text.Length == 0 ? "（未选择）" : "= " + text;
        }

        public AdvancedFilterCondition Collect()
        {
            var condition = new AdvancedFilterCondition
            {
                Column = CurrentColumn,
                Enabled = _enabled.IsChecked == true,
                TextMode = _textMode?.SelectedIndex == 1 ? TextFilterMode.Contains : TextFilterMode.Exact,
                Text = _text?.Text?.Trim() ?? "",
                Min = _min?.Text?.Trim() ?? "",
                Max = _max?.Text?.Trim() ?? "",
                PhoneSide = _phone?.SelectedItem as string ?? "",
                PadSide = _pad?.SelectedItem as string ?? "",
                Compatibility = _compat?.SelectedIndex switch
                {
                    1 => CompatFilterMode.Installable,
                    2 => CompatFilterMode.Adapted,
                    _ => CompatFilterMode.Off,
                },
                Selected = _multi.Where(c => c.IsChecked == true).Select(c => c.Content?.ToString() ?? "").ToList(),
            };

            if (condition.Column is AdvancedFilterColumn.Version or AdvancedFilterColumn.MinOs)
            {
                if (_versionMode?.SelectedIndex == 0)
                {
                    condition.Text = _versionExact?.Text?.Trim() ?? "";
                    condition.Min = "";
                    condition.Max = "";
                }
                else
                {
                    condition.Text = "";
                }
            }

            return condition;
        }
    }
}
