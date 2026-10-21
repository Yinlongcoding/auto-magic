using AM.Desktop.Models;
using AM.Desktop.Services;

namespace AM.Desktop.ViewModels;

/// <summary>总览。统计数字为固定示例，真实统计推迟到未来版本。</summary>
public sealed class OverviewViewModel
{
    public IReadOnlyList<OverviewStat> Stats => SampleData.Stats;

    public IReadOnlyList<CollectionBatch> Running =>
        SampleData.Batches.Where(b => b.State.IsRunning()).ToList();

    public IReadOnlyList<CollectionBatch> WaitingForListing =>
        SampleData.Batches.Where(b => b.State == BatchState.Collected).ToList();

    public IReadOnlyList<CollectionBatch> Finished =>
        SampleData.Batches.Where(b => b.State.IsFinished()).ToList();
}
