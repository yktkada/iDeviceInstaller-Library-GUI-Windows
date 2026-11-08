using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using IdeviceGui.ViewModels;

namespace IdeviceGui.Views;

/// <summary>安装到…：只列在线设备，支持刷新 + 按现有 2 s 设备轮询实时更新。</summary>
internal sealed partial class InstallToWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaisePropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _checkedUdids = new(StringComparer.OrdinalIgnoreCase);
    private string _lastChoiceSignature = "";

    internal sealed class Choice : ObservableObject
    {
        private bool _isChecked;
        public required string Udid { get; init; }
        public required string Display { get; init; }
        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }
    }

    public ObservableCollection<Choice> Choices { get; } = new();

    public IReadOnlyList<string> SelectedUdids =>
        Choices.Where(c => c.IsChecked).Select(c => c.Udid).ToList();

    public bool AllSelected
    {
        get => Choices.Count > 0 && Choices.All(c => c.IsChecked);
        set
        {
            foreach (var choice in Choices) choice.IsChecked = value;
            RaisePropertyChanged(nameof(AllSelected));
        }
    }

    internal InstallToWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = this;
        foreach (var choice in Choices) choice.PropertyChanged += OnChoiceChanged;
        RefreshChoices();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshChoices();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Choice.IsChecked))
        {
            RaisePropertyChanged(nameof(AllSelected));
            _checkedUdids.Clear();
            foreach (var choice in Choices)
                if (choice.IsChecked) _checkedUdids.Add(choice.Udid);
        }
    }

    private void RefreshChoices()
    {
        var online = _vm.Devices.Where(d => d.Device.Online).ToList();
        var signature = string.Join("|", online.Select(d => d.Device.Udid + "\u001f" + d.Device.ShortName));
        if (string.Equals(signature, _lastChoiceSignature, StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(AllSelected));
            return;
        }
        _lastChoiceSignature = signature;
        var existing = Choices.ToDictionary(c => c.Udid, StringComparer.OrdinalIgnoreCase);
        foreach (var choice in Choices) choice.PropertyChanged -= OnChoiceChanged;
        Choices.Clear();
        foreach (var device in online)
        {
            var udid = device.Device.Udid;
            var display = device.Device.ShortName;
            var choice = new Choice
            {
                Udid = udid,
                Display = display,
                IsChecked = existing.TryGetValue(udid, out var old) ? old.IsChecked : _checkedUdids.Contains(udid),
            };
            choice.PropertyChanged += OnChoiceChanged;
            Choices.Add(choice);
        }
        RaisePropertyChanged(nameof(AllSelected));
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _vm.RefreshDevicesForInstallDialogAsync();
        RefreshChoices();
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
