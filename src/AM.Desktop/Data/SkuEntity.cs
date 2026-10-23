using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AM.Desktop.Data;

/// <summary>
/// SKU 表实体。
/// </summary>
[Table("Skus")]
public sealed class SkuEntity
{
    /// <summary>主键（自增）。</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>所属商品 ID。</summary>
    public required int ProductId { get; set; }

    /// <summary>来源 SKU ID（字符串）。</summary>
    [Required]
    [MaxLength(128)]
    public required string SourceSkuId { get; set; }

    /// <summary>轴选项值（JSON 数组）。</summary>
    [Column(TypeName = "TEXT")]
    public string? OptionValuesJson { get; set; }

    /// <summary>来源价格（抓取到的原始价格）。</summary>
    public decimal? SourcePrice { get; set; }

    /// <summary>加价公式结果（目标价格）。</summary>
    public decimal? TargetPrice { get; set; }

    /// <summary>SKU 专属图片 URL。</summary>
    [MaxLength(1024)]
    public string? ImageUrl { get; set; }

    /// <summary>库存数量（如果采集到）。</summary>
    public int? Stock { get; set; }

    /// <summary>外键导航。</summary>
    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }
}
