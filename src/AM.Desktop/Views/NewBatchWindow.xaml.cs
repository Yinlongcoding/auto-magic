using AM.Desktop.Models;
using AM.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace AM.Desktop.Views;

public partial class NewBatchWindow : FluentWindow
{
    private readonly NewBatchViewModel _viewModel = new();

    public NewBatchWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.CloseRequested += (_, _) => DialogResult = true;
    }

    public CollectionBatch? Result => _viewModel.Result;

    private void OnCancel(object sender, System.Windows.RoutedEventArgs e) => DialogResult = false;
}
