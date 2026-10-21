using System.Windows.Controls;
using AM.Desktop.ViewModels;

namespace AM.Desktop.Views;

public partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
        DataContext = new OverviewViewModel();
    }
}
