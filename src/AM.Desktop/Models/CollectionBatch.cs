using CommunityToolkit.Mvvm.ComponentModel;

namespace AM.Desktop.Models;

/// <summary>
/// 一个采集批次。定价参数在采集时填写并冻结，批次不允许合并，一次上架只处理一个批次。
/// </summary>
public sealed partial class CollectionBatch : ObservableObject
{
    public required string Id { get; init; }
    public required string Keyword { get; init; }
    public required string CategoryPath { get; init; }

    /// <summary>新建时冻结的 Ozon 类目和类型。</summary>
    public long DescriptionCategoryId { get; init; }
    public long TypeId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string PricingSummary { get; init; }

    public int TargetCount { get; init; }

    /// <summary>新建时选的排序方式（界面文案），下发采集时转成协议取值。</summary>
    public string Sort { get; init; } = "综合";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ProgressText), nameof(ListText))]
    private BatchState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ListText))]
    private int _collectPass = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ListText))]
    private int _collectedCount;

    /// <summary>第一遍健康检查超阈值时的提示，如"采集可能失效"。</summary>
    [ObservableProperty]
    private string? _warning;

    // 上架
    public int SkuCount { get; init; }
    public int BlockingCount { get; init; }
    public int AbandonedCount { get; init; }
    public int CopyFailedCount { get; init; }
    public int SubmittedCount { get; init; }
    public int SucceededCount { get; init; }

    public string StateText => State.DisplayName();

    public string ProgressText => State == BatchState.Collecting
        ? $"第 {CollectPass} 遍 {CollectedCount}/{TargetCount}"
        : $"{CollectedCount}/{TargetCount}";

    /// <summary>批次列表第二行：状态和最关键的数字。</summary>
    public string ListText => State switch
    {
        BatchState.Collecting => $"采集中 · {ProgressText}",
        BatchState.Listing when BlockingCount > 0 => $"上架中 · 阻断 {BlockingCount}",
        BatchState.Completed => $"已完成 · 成功 {SucceededCount}/{SubmittedCount}",
        _ => $"{StateText} · {ProgressText}",
    };

    public string DisplayName => $"{Keyword} · {CategoryPath} · {CreatedAt:MM-dd HH:mm}";
}
