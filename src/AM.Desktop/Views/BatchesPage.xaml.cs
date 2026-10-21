using System.Windows.Controls;
using AM.Desktop.ViewModels;

namespace AM.Desktop.Views;

public partial class BatchesPage : Page
{
    public BatchesPage()
    {
        InitializeComponent();
        DataContext = new BatchesViewModel();
    }
}
