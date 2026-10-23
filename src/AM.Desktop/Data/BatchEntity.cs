namespace AM.Desktop.Data;

/// <summary>
/// 批次实体（数据库表）
/// </summary>
public class BatchEntity
{
    public int Id { get; set; }
    public string BatchId { get; set; } = string.Empty;
    public string Keyword { get; set; } = string.Empty;
    public string DataJson { get; set; } = string.Empty; // CollectionBatch序列化
    public string ProductsJson { get; set; } = string.Empty; // 商品列表序列化
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// 任务记录实体（数据库表）
/// </summary>
public class HistoryEntity
{
    public int Id { get; set; }
    public DateTime Time { get; set; }
    public string Batch { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
}
