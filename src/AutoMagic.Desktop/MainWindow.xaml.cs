using System.ComponentModel;
using System.Windows;
using AutoMagic.Desktop.ViewModels;

namespace AutoMagic.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
        OzonApiKeyPasswordBox.Password = _viewModel.OzonApiKey;
        QwenApiKeyPasswordBox.Password = _viewModel.QwenApiKey;
    }

    private void OnOzonApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.OzonApiKey = passwordBox.Password;
        }
    }

    private async void OnClearOzonCredentials(object sender, RoutedEventArgs e)
    {
        OzonApiKeyPasswordBox.Clear();
        await _viewModel.ClearTemporaryOzonCredentialsAsync();
    }

    private void OnQwenApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.QwenApiKey = passwordBox.Password;
        }
    }

    private async void OnClearQwenCredentials(object sender, RoutedEventArgs e)
    {
        QwenApiKeyPasswordBox.Clear();
        await _viewModel.ClearTemporaryQwenCredentialsAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy || _viewModel.IsRunningQwenMapping)
        {
            var choice = MessageBox.Show(
                "搜索采集或AI映射仍在进行，确定要退出吗？",
                "Auto Magic",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        }

        base.OnClosing(e);
    }
}
