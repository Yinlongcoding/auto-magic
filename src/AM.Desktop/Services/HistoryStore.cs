using System.Collections.Specialized;
using AM.Desktop.Data;
using AM.Desktop.Models;

namespace AM.Desktop.Services;

/// <summary>
/// 任务记录持久化：启动时把库里的记录装进 BatchStore.History，之后新插入的记录自动写库。
/// 各处照旧往 BatchStore.History 插记录，不用关心持久化。
/// </summary>
public class HistoryStore
{
    private readonly HistoryRepository _repository;

    public HistoryStore(HistoryRepository repository)
    {
        _repository = repository;
    }

    /// <summary>写库失败时触发，参数是错误信息。</summary>
    public event Action<string>? SaveFailed;

    /// <summary>从数据库加载所有任务记录（新的在前），然后开始监听新记录。须在 UI 线程调用。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var entries = await _repository.LoadAllAsync();

            // 加载期间可能已有新记录插到前面，库里的旧记录接在后面
            foreach (var entry in entries)
            {
                BatchStore.History.Add(entry);
            }
        }
        finally
        {
            // 加载失败也要监听，否则之后的记录都不落库
            BatchStore.History.CollectionChanged += OnHistoryChanged;
        }
    }

    private async void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (HistoryEntry entry in e.NewItems)
        {
            try
            {
                await _repository.SaveAsync(entry);
            }
            catch (Exception error)
            {
                SaveFailed?.Invoke(error.Message);
            }
        }
    }
}
