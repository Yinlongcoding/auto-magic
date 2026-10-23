using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AM.Desktop.Data;

/// <summary>
/// 商品表实体。
/// </summary>
[Table("Products")]
public sealed class ProductEntity
{
    /// <summary>主键（自增）。</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>所属批次 ID。</summary>
    [Required]
    [MaxLength(36)]
    public required string BatchId { get; set; }

    /// <summary>来源商品 ID（字符串）。</summary>
    [Required]
    [MaxLength(128)]
    public required string SourceOfferId { get; set; }

    /// <summary>商品标题。</summary>
    [Required]
    [MaxLength(512)]
    public required string Title { get; set; }

    /// <summary>品牌。</summary>
    [MaxLength(128)]
    public string? Brand { get; set; }

    /// <summary>重量（kg）。</summary>
    public decimal? WeightKg { get; set; }

    /// <summary>价格类型（固定价 / 区间价）。</summary>
    [MaxLength(32)]
    public string? PriceType { get; set; }

    /// <summary>主图 URLs（JSON 数组）。</summary>
    [Column(TypeName = "TEXT")]
    public string? MainImagesJson { get; set; }

    /// <summary>详情页 URL。</summary>
    [MaxLength(1024)]
    public string? DetailUrl { get; set; }

    /// <summary>属性列表（JSON）。</summary>
    [Column(TypeName = "TEXT")]
    public string? AttributesJson { get; set; }

    /// <summary>轴列表（JSON）。</summary>
    [Column(TypeName = "TEXT")]
    public string? AxesJson { get; set; }

    /// <summary>SKU 列表（导航属性）。</summary>
    public ICollection<SkuEntity> Skus { get; set; } = new List<SkuEntity>();

    /// <summary>外键导航。</summary>
    [ForeignKey(nameof(BatchId))]
    public BatchEntity? Batch { get; set; }
}
