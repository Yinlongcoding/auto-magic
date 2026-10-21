using System.Windows.Controls;
using AM.Desktop.ViewModels;

namespace AM.Desktop.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }
}
