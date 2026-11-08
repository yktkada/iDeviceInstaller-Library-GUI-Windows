using System.Collections.ObjectModel;
using System.Windows;
using IdeviceGui.ViewModels;

namespace IdeviceGui.Views;

internal sealed partial class GroupManagerWindow : Window
{
    private readonly MainViewModel _vm;

    public ObservableCollection<string> Groups { get; } = new();

    public GroupManagerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = this;
        Refresh();
    }

    private void Refresh(string? select = null)
    {
        var current = select ?? GroupsList.SelectedItem as string;
        Groups.Clear();
        foreach (var option in _vm.GroupFilterOptions.Skip(1)) Groups.Add(option.Name);
        if (current is not null) GroupsList.SelectedItem = Groups.FirstOrDefault(
            g => string.Equals(g, current, StringComparison.OrdinalIgnoreCase));
        if (GroupsList.SelectedItem is null && Groups.Count > 0) GroupsList.SelectedIndex = 0;
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is not string oldName)
        {
            MessageBox.Show(this, "请先选择一个分组。", "管理分组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new GroupNameWindow("重命名分组", oldName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var error = _vm.RenameGroup(oldName, dialog.GroupName);
        if (error is not null)
        {
            MessageBox.Show(this, error, "管理分组", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Refresh(dialog.GroupName);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is not string name)
        {
            MessageBox.Show(this, "请先选择一个分组。", "管理分组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"删除分组「{name}」？" + Environment.NewLine + "只删除分组关系，不删除资料库文件。",
                "管理分组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var error = _vm.DeleteGroup(name);
        if (error is not null)
        {
            MessageBox.Show(this, error, "管理分组", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Refresh();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
