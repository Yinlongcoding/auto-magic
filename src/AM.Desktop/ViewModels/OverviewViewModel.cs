using AM.Desktop.Models;
using AM.Desktop.Services;

namespace AM.Desktop.ViewModels;

/// <summary>
/// 总览。批次数取自本次运行的内存数据；成功率和上架统计要等数据层（M2）和提交模块，暂显示"—"。
/// </summary>
public sealed class OverviewViewModel
{
    public IReadOnlyList<OverviewStat> Stats =>
    [
        new("采集批次", BatchStore.Batches.Count.ToString()),
        new("采集成功率", "—"),
        new("上架次数", "—"),
        new("上架成功率", "—"),
    ];

    public IReadOnlyList<CollectionBatch> Running =>
        BatchStore.Batches.Where(b => b.State.IsRunning()).ToList();

    public IReadOnlyList<CollectionBatch> WaitingForListing =>
        BatchStore.Batches.Where(b => b.State == BatchState.Collected).ToList();

    public IReadOnlyList<CollectionBatch> Finished =>
        BatchStore.Batches.Where(b => b.State.IsFinished()).ToList();
}
