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
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            var choice = MessageBox.Show(
                "搜索采集仍在进行，确定要退出吗？",
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
