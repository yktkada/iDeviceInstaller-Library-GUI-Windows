using IdeviceGui.Services;

using IdeviceGui.ViewModels.Library;

namespace IdeviceGui.ViewModels;

/// <summary>设备应用列表的一行（只读回读快照；不含系统 App）。</summary>
internal sealed class DeviceAppRow : ObservableObject
{
    private object? _icon;
    private LibraryMarker _marker;
    private bool _isArmed;
    private OperationVisual _visual;

    public required string BundleId { get; init; }
    public required string Name { get; init; }
    public required string VersionText { get; init; }

    /// <summary>已解码并 Freeze 的图标；没有图标时为 null（界面显示占位）。</summary>
    public object? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>统一操作状态（队列快照解析）；设备应用行与队列/资料库共用。</summary>
    public OperationVisual Visual
    {
        get => _visual;
        set
        {
            if (!SetProperty(ref _visual, value)) return;
            OnPropertyChanged(nameof(RowBrush));
        }
    }

    public System.Windows.Media.Brush RowBrush => _visual.ToBrush();

    /// <summary>兼容旧 UI 的 Marker（新样式已改用 Visual）。</summary>
    public LibraryMarker Marker
    {
        get => _marker;
        set => SetProperty(ref _marker, value);
    }

    /// <summary>资料库式单击待确认：第一次单击深蓝（armed），400 ms 内第二次单击执行卸载。</summary>
    public bool IsArmed
    {
        get => _isArmed;
        set => SetProperty(ref _isArmed, value);
    }

    // LibraryRowStyle 的组行绑定契约；设备应用没有组状态。
    public bool IsGroup => false;
    public bool IsGroupEnd => false;

    public string Display => $"{Name}   {VersionText}   {BundleId}";

    public override string ToString() => Display;
}
