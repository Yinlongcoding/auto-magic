using System.Collections.ObjectModel;
using AM.Desktop.Models;
using AM.Desktop.Services;

namespace AM.Desktop.ViewModels;

/// <summary>任务记录。详情内容未定，按钮暂不可用。</summary>
public sealed class HistoryViewModel
{
    public ObservableCollection<HistoryEntry> Entries => BatchStore.History;
}
