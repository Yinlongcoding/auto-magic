using System.Collections.ObjectModel;
using System.ComponentModel;
using AM.Desktop.Models;
using AM.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AM.Desktop.ViewModels;

/// <summary>
/// 采集上架：左边批次列表，右边选中批次的四个步骤页签（采集、待处理、文案、提交）。
/// 新建采集在弹窗中完成。同一时间只能跑一个采集，批次不允许合并。
/// </summary>
public sealed partial class BatchesViewModel : ObservableObject
{
    public BatchesViewModel()
    {
        _selectedBatch = Batches.FirstOrDefault();
        _selectedTabIndex = _selectedBatch is null ? 0 : DefaultTab(_selectedBatch);
        WatchBatch(null, _selectedBatch);

        // 弱事件：页面每次导航都会新建，服务和批次是长寿对象，不能被它们拽住。
        PropertyChangedEventManager.AddHandler(Collection, (_, _) => Refresh(), string.Empty);
    }

    private static CollectionService Collection => App.Collection;

    public ObservableCollection<CollectionBatch> Batches => SampleData.Batches;

    [ObservableProperty]
    private bool _isBatchListOpen = true;

    [ObservableProperty]
    private CollectionBatch? _selectedBatch;

    [ObservableProperty]
    private int _selectedTabIndex;

    public bool IsBatchSelected => SelectedBatch is not null;
    public bool HasNoBatch => SelectedBatch is null;

    public CollectionBatch? Running => Batches.FirstOrDefault(b => b.State == BatchState.Collecting);
    public bool CanCreate => Running is null && Collection.IsPluginConnected;
    public string NewBatchTip => Running is { } b ? $"已有采集在进行：{b.Keyword}"
        : !Collection.IsPluginConnected ? "浏览器插件未连接"
        : "新建采集";

    public string PluginStatus => Collection.IsPluginConnected
        ? $"插件已连接{(Collection.PluginVersion is { } v ? $" · {v}" : "")}"
        : "插件未连接：请打开 Chrome 并启用 AM 采集插件";

    // 页签灰态：采集完成前后三个不可用；待处理清零后提交才可用；已中断只能看采集。
    public bool IsCollected => SelectedBatch?.State is BatchState.Collected or BatchState.Listing or BatchState.Completed;
    public bool CanSubmit => IsCollected && SelectedBatch!.BlockingCount == 0;

    public bool IsCollecting => SelectedBatch?.State == BatchState.Collecting;
    public bool IsReadOnly => SelectedBatch?.State.IsFinished() == true;
    public bool HasWarning => SelectedBatch?.Warning is not null;

    public string? ReadOnlyMessage => SelectedBatch?.State switch
    {
        BatchState.Interrupted => "此批次已中断，采集数据已丢弃，只读。",
        BatchState.Completed => "此批次已完成，只读。",
        _ => null,
    };

    public string CollectHeader => SelectedBatch is { } b ? $"采集 {b.ProgressText}" : "采集";
    public string PendingHeader => IsCollected && SelectedBatch!.BlockingCount > 0 ? $"待处理 {SelectedBatch.BlockingCount}" : "待处理";
    public string CopyHeader => IsCollected && SelectedBatch!.CopyFailedCount > 0 ? $"文案 {SelectedBatch.CopyFailedCount} 失败" : "文案";
    public string SubmitHeader => CanSubmit && SelectedBatch!.SkuCount > 0
        ? $"提交 {SelectedBatch.SucceededCount}/{SelectedBatch.SkuCount}"
        : "提交";

    public IReadOnlyList<CollectedProduct> Products => SelectedBatch switch
    {
        null => [],
        { State: BatchState.Interrupted } => [],
        { } b => SampleData.ProductsFor(b),
    };

    partial void OnSelectedBatchChanged(CollectionBatch? oldValue, CollectionBatch? newValue)
    {
        WatchBatch(oldValue, newValue);
        Refresh();
        SelectedTabIndex = newValue is null ? 0 : DefaultTab(newValue);
    }

    [RelayCommand]
    private void ToggleBatchList() => IsBatchListOpen = !IsBatchListOpen;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task NewBatchAsync()
    {
        if (DialogService.ShowNewBatch() is not { } batch)
        {
            return;
        }

        Batches.Insert(0, batch);
        SampleData.History.Insert(0, new(DateTime.Now, batch.Keyword, "开始采集", $"目标 {batch.TargetCount} 个商品"));
        SelectedBatch = batch;

        // 发送失败时服务已把批次标为中断。弹窗期间插件断开或已有采集，这里同样按中断处理。
        if (!await Collection.StartAsync(batch) && batch.State == BatchState.Collecting)
        {
            batch.State = BatchState.Interrupted;
            SampleData.History.Insert(0, new(DateTime.Now, batch.Keyword, "中断采集，任务结束", "插件未连接，未开始"));
        }

        Refresh();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Running is not { } batch)
        {
            return;
        }

        var confirmed = await DialogService.ConfirmAsync(
            "停止采集",
            "停止将丢弃本次已采集的全部数据，任务结束。",
            "停止");
        if (!confirmed)
        {
            return;
        }

        await Collection.StopAsync("用户停止");
        Refresh();
    }

    /// <summary>选中批次时跳到最可能要处理的页签。</summary>
    private static int DefaultTab(CollectionBatch batch) => batch.State switch
    {
        BatchState.Collected or BatchState.Listing when batch.BlockingCount > 0 => 1,
        BatchState.Collected or BatchState.Listing => 3,
        BatchState.Completed => 3,
        _ => 0,
    };

    /// <summary>选中批次的进度和状态变化要刷新页签标题等派生属性。</summary>
    private void WatchBatch(CollectionBatch? oldValue, CollectionBatch? newValue)
    {
        if (oldValue is not null)
        {
            PropertyChangedEventManager.RemoveHandler(oldValue, OnBatchChanged, string.Empty);
        }

        if (newValue is not null)
        {
            PropertyChangedEventManager.AddHandler(newValue, OnBatchChanged, string.Empty);
        }
    }

    private void OnBatchChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        NewBatchCommand.NotifyCanExecuteChanged();
    }
}
