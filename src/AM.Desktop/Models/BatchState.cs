namespace AM.Desktop.Models;

/// <summary>
/// 采集批次状态。界面层暂用，建库时再对齐 09 号 DDL（需用户确认）。
/// </summary>
public enum BatchState
{
    /// <summary>采集中。同一时间只有一个。</summary>
    Collecting,

    /// <summary>采集完成，待上架。</summary>
    Collected,

    /// <summary>上架中（处理阻断、文案、提交）。</summary>
    Listing,

    /// <summary>已完成，只读。</summary>
    Completed,

    /// <summary>已中断（停止、关闭、崩溃、插件断连），只读。</summary>
    Interrupted,
}

public static class BatchStateExtensions
{
    public static string DisplayName(this BatchState state) => state switch
    {
        BatchState.Collecting => "采集中",
        BatchState.Collected => "待上架",
        BatchState.Listing => "上架中",
        BatchState.Completed => "已完成",
        BatchState.Interrupted => "已中断",
        _ => state.ToString(),
    };

    public static bool IsRunning(this BatchState state) =>
        state is BatchState.Collecting or BatchState.Listing;

    public static bool IsListable(this BatchState state) =>
        state is BatchState.Collected or BatchState.Listing;

    public static bool IsFinished(this BatchState state) =>
        state is BatchState.Completed or BatchState.Interrupted;
}
