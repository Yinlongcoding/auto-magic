using System.Windows.Controls;
using AM.Desktop.ViewModels;

namespace AM.Desktop.Views;

public partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        DataContext = new HistoryViewModel();
    }
}
