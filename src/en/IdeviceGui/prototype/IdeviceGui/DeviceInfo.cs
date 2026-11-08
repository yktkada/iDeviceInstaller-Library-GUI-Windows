using System.Globalization;
using IdeviceGui.Services;
using IdeviceGui.Services.Diagnostics;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;

namespace IdeviceGui;

/// <summary>
/// 一台设备的快照信息。属性可写是为了"Update in place"（01 §6.6 / 进度 §5 第 5 条：
/// 刷新设备不允许 Devices.Clear() 重建，否则正在跑的泵会变孤儿）。
/// <para>信息行（工具栏那行）按 <see cref="Fields"/> 的字段开关与顺序拼出来；默认集见
/// <see cref="DeviceInfoFields.Default"/>（2026-11-01 用户定稿）。</para>
/// </summary>
internal sealed class DeviceInfo : ObservableObject
{
    private string _name = "";
    private string _product = "";
    private string _version = "";
    private string _build = "";
    private string _cpuArch = "";
    private string _color = "";
    private string _ecid = "";
    private string _serial = "";
    private string _modelNumber = "";
    private string _hardwareModel = "";
    private bool _online = true;
    private DiskUsage _space = DiskUsage.Unknown;
    private BatteryUsage _usage = BatteryUsage.Unknown;
    private BatteryHealth _health = BatteryHealth.Unknown;
    private BatterySpec _spec = BatterySpec.Unknown;

    /// <summary>信息行字段配置（M4-b4 接口预留：设置面板以后接；启动时由 ViewModel 从 Settings 读入）。</summary>
    public static IReadOnlyList<DeviceInfoFieldSetting> Fields { get; set; } = DeviceInfoFields.Default;

    public required string Udid { get; init; }

    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            RaiseDerived();
        }
    }

    public string Product
    {
        get => _product;
        set
        {
            if (!SetProperty(ref _product, value)) return;
            RaiseDerived();
        }
    }

    public string Version
    {
        get => _version;
        set
        {
            if (!SetProperty(ref _version, value)) return;
            RaiseDerived();
        }
    }

    public string Build
    {
        get => _build;
        set
        {
            if (!SetProperty(ref _build, value)) return;
            RaiseDerived();
        }
    }

    public string CpuArch
    {
        get => _cpuArch;
        set => SetProperty(ref _cpuArch, value);
    }

    /// <summary>原始 <c>DeviceColor</c>（<c>#e1e4e3</c> 或 <c>white</c>）。</summary>
    public string Color
    {
        get => _color;
        set
        {
            if (!SetProperty(ref _color, value)) return;
            RaiseDerived();
        }
    }

    /// <summary>ECID（<c>UniqueChipID</c>，十进制字符串）。</summary>
    public string Ecid
    {
        get => _ecid;
        set
        {
            if (!SetProperty(ref _ecid, value)) return;
            RaiseDerived();
        }
    }

    public string Serial
    {
        get => _serial;
        set
        {
            if (!SetProperty(ref _serial, value)) return;
            RaiseDerived();
        }
    }

    public string ModelNumber
    {
        get => _modelNumber;
        set => SetProperty(ref _modelNumber, value);
    }

    public string HardwareModel
    {
        get => _hardwareModel;
        set => SetProperty(ref _hardwareModel, value);
    }

    public bool Online
    {
        get => _online;
        set
        {
            if (!SetProperty(ref _online, value)) return;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => Online ? "Online" : "Offline";

    /// <summary>容量（<c>com.apple.disk_usage</c>）；未知时信息行不显示容量段。</summary>
    public DiskUsage Space => _space;

    public BatteryUsage Usage => _usage;

    public BatteryHealth Health => _health;

    public BatterySpec Spec => _spec;

    /// <summary>版本段：有 Build 就 <c>8.1.2(12B440)</c>，没有就只显示版本。</summary>
    public string VersionText => Build.Length > 0 ? $"{Version}({Build})" : Version;

    /// <summary>容量段：<c>可用 19.0 / 58.1 GB</c>（1024 进制、1 位小数）；未知给空串。</summary>
    public string SpaceText => _space.Ok
        ? $"Free {Gigabytes(_space.FreeBytes)} / {Gigabytes(_space.TotalBytes)} GB"
        : "";

    /// <summary>颜色段：hex 原样显示（色块在界面层画），命名色截断到 8 字符。</summary>
    public string ColorText
    {
        get
        {
            if (_color.Length == 0) return "";
            if (IsHexColor(_color)) return _color;
            return _color.Length <= 8 ? _color : _color[..8] + "…";
        }
    }

    /// <summary>颜色是 hex 时才画色块（与 iOS 版本无关，按值判断）。</summary>
    public bool HasColorSwatch => IsHexColor(_color);

    /// <summary>色块画刷（<c>#RRGGBB</c> / <c>#RGB</c> / 省略 <c>#</c>）；不是 hex 给 null。</summary>
    public object? ColorBrush
    {
        get
        {
            if (!IsHexColor(_color)) return null;
            var hex = _color.StartsWith('#') ? _color[1..] : _color;
            if (hex.Length == 3)
            {
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            }

            try
            {
                var brush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#" + hex));
                brush.Freeze();
                return brush;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>电池健康段：<c>92.5%</c> / <c>101.9%(不可信)</c>；取不到给空串（整段不显示）。</summary>
    public string BatteryHealthText => _health.HasValue ? _health.Display : "";

    // ── 「更多 ▾」菜单用文本（模板不引转换器，直接给字符串；取不到统一给「—」） ──────────
    // 2026-11-01 用户要求：菜单是「值 + 点击复制」一列；ECID 拆 hex/十进制两行；循环次数不再单列（健康度行已含）。

    public string UsageMenuText => "Battery: " + (_usage.HasValue ? _usage.Display : "—");

    public string HealthMenuText => _health.HasValue && _health.Ratio is not null
        ? _health.ToolTip
        : "Maximum battery capacity: —";

    public string ManufacturerMenuText => "Manufacturer: " + (_spec.Manufacturer.Length > 0 ? _spec.Manufacturer : "—");

    public string BatterySerialMenuText => "Battery serial number: " + (_spec.Serial.Length > 0 ? _spec.Serial : "—");

    /// <summary>ECID 十六进制（16 位大写、无前缀、左侧补零；例 <c>0000039BDC184348</c>）；空/非法给空串。</summary>
    public string EcidHex =>
        long.TryParse(_ecid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("X16", CultureInfo.InvariantCulture)
            : "";

    public string EcidHexMenuText => "ECID(hex)：" + (EcidHex.Length > 0 ? EcidHex : "—");

    public string EcidDecimalMenuText => "ECID(dec)：" + (_ecid.Length > 0 ? _ecid : "—");

    public string SerialMenuText => "Serial number: " + (_serial.Length > 0 ? _serial : "—");

    public string UdidMenuText => "UDID：" + (Udid.Length > 0 ? Udid : "—");

    public string ModelNumberMenuText => "Part number: " + (_modelNumber.Length > 0 ? _modelNumber : "—");

    public string HardwareModelMenuText => "Hardware model: " + (_hardwareModel.Length > 0 ? _hardwareModel : "—");

    /// <summary>信息行文本（工具栏）：按字段配置拼，空段自动跳过。</summary>
    public string InfoLine => string.Join(" · ", Segments());

    /// <summary>信息行 ToolTip：完整设备信息（含序列号 / UDID / 板型 / 部件号 / 电池明细）。</summary>
    public string InfoToolTip
    {
        get
        {
            var lines = new List<string> { InfoLine };
            if (_serial.Length > 0) lines.Add($"Serial number: {_serial}");
            if (Udid.Length > 0) lines.Add($"UDID：{Udid}");
            if (_modelNumber.Length > 0) lines.Add($"Part number: {_modelNumber}");
            if (_hardwareModel.Length > 0) lines.Add($"Hardware model: {_hardwareModel}");
            if (_ecid.Length > 0) lines.Add($"ECID：{(EcidHex.Length > 0 ? EcidHex : _ecid)}");
            if (_cpuArch.Length > 0) lines.Add($"CPU：{_cpuArch}");
            if (_health.HasValue) lines.Add(_health.ToolTip);
            return string.Join("\n", lines);
        }
    }

    /// <summary>设备切换条 / 队列标题用的短名。</summary>
    public string ShortName => $"{Name} (iOS {Version})";

    /// <summary>兼容旧调用方（切换条 ToolTip 等）；内容与 <see cref="InfoLine"/> 一致。</summary>
    public string Display => InfoLine.Length > 0 ? InfoLine : Name;

    /// <summary>设备信息字段配置变了（设置窗口应用后调用）→ 重算信息行文本。</summary>
    public void RefreshInfoLine() => RaiseDerived();

    public void SetSpace(DiskUsage space)
    {
        _space = space;
        RaiseDerived();
    }

    public void SetBattery(BatteryUsage usage, BatteryHealth health, BatterySpec spec)
    {
        _usage = usage;
        _health = health;
        _spec = spec;
        RaiseDerived();
    }

    /// <summary>字段配置变化后由 ViewModel 调一次，刷新信息行与 ToolTip。</summary>
    public void RefreshInfo() => RaiseDerived();

    // 让切换条 / 列表的 UIA 名称与界面文字一致（AGENTS §6.3）
    public override string ToString() => ShortName;

    private IEnumerable<string> Segments()
    {
        var visible = DeviceInfoFields.OrderedVisible(Fields);
        var productVisible = visible.Contains(DeviceInfoField.Product);
        var modelLabel = DeviceScreen.ModelLabel(HardwareModel, Product, Name);

        foreach (var field in visible)
        {
            var text = field switch
            {
                // 名称与营销名重复时（如 DeviceName=iPad / 型号=iPad 2）只留“型号(ProductType)”段
                DeviceInfoField.Name => productVisible && IsNamePrefixOfModel(Name, modelLabel) ? "" : Name,
                DeviceInfoField.Product => modelLabel,
                DeviceInfoField.Version => VersionText,
                DeviceInfoField.Capacity => SpaceText,
                DeviceInfoField.Color => ColorText,
                DeviceInfoField.BatteryHealth => BatteryHealthText,
                DeviceInfoField.Ecid => EcidHex.Length > 0 ? $"ECID {EcidHex}" : "",
                _ => "",
            };

            if (text.Length > 0) yield return text;
        }
    }

    private static bool IsNamePrefixOfModel(string name, string model)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        var m = (model ?? "").Trim();
        return m.Length > n.Length
            && m.StartsWith(n, StringComparison.OrdinalIgnoreCase)
            && (m[n.Length] == '(' || m[n.Length] == ' ' || m[n.Length] == '（');
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(SpaceText));
        OnPropertyChanged(nameof(ColorText));
        OnPropertyChanged(nameof(HasColorSwatch));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(BatteryHealthText));
        OnPropertyChanged(nameof(UsageMenuText));
        OnPropertyChanged(nameof(HealthMenuText));
        OnPropertyChanged(nameof(ManufacturerMenuText));
        OnPropertyChanged(nameof(BatterySerialMenuText));
        OnPropertyChanged(nameof(EcidHex));
        OnPropertyChanged(nameof(EcidHexMenuText));
        OnPropertyChanged(nameof(EcidDecimalMenuText));
        OnPropertyChanged(nameof(SerialMenuText));
        OnPropertyChanged(nameof(UdidMenuText));
        OnPropertyChanged(nameof(ModelNumberMenuText));
        OnPropertyChanged(nameof(HardwareModelMenuText));
        OnPropertyChanged(nameof(InfoLine));
        OnPropertyChanged(nameof(InfoToolTip));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ShortName));
        OnPropertyChanged(nameof(Usage));
        OnPropertyChanged(nameof(Space));
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(Spec));
    }

    private static string Gigabytes(long bytes) =>
        (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);

    private static bool IsHexColor(string raw)
    {
        var hex = raw.StartsWith('#') ? raw[1..] : raw;
        return hex.Length is 3 or 6 && hex.All(Uri.IsHexDigit);
    }
}
