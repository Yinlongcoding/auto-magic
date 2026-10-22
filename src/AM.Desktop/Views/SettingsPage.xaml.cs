using System.Windows;
using System.Windows.Controls;
using AM.Desktop.ViewModels;

namespace AM.Desktop.Views;

public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel = new();

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.ApiKeyCleared += () => OzonApiKeyBox.Password = "";
    }

    // 密码框不做双向绑定，只把输入同步给视图模型。
    private void OnOzonApiKeyChanged(object sender, RoutedEventArgs e) => _viewModel.ApiKeyInput = OzonApiKeyBox.Password;
}
