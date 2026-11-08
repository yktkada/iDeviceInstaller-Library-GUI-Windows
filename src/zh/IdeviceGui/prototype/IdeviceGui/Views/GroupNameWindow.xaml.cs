using System.Windows;

namespace IdeviceGui.Views;

internal sealed partial class GroupNameWindow : Window
{
    public GroupNameWindow(string title, string initial)
    {
        InitializeComponent();
        Title = title;
        NameBox.Text = initial;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string GroupName => NameBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (GroupName.Length == 0)
        {
            MessageBox.Show(this, "分组名不能为空。", "分组", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
