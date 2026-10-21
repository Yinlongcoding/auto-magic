namespace AM.Desktop.Models;

/// <summary>任务记录表格的一行：什么时间、哪个批次、什么操作、什么结果。</summary>
public sealed record HistoryEntry(DateTime Time, string Batch, string Action, string Result);
