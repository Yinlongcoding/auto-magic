using AM.Desktop.Views;
using Wpf.Ui.Controls;

namespace AM.Desktop;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        App.Theme.Apply(Services.ThemeService.Default, this);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(OverviewPage));
    }
}
