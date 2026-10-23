using System.Security.Cryptography;
using System.Text;
using AM.Bridge;
using AM.Core.Pricing;
using AM.Desktop.Models;

namespace AM.Desktop.Services;

/// <summary>
/// 提交服务：将采集批次转换为 Ozon API 格式。
/// 目前只做数据组装和验证，不调用真实 API。
/// </summary>
public sealed class SubmitService
{
    /// <summary>
    /// 将批次转换为 Ozon 提交格式并预览（按商品分组）。
    /// </summary>
    public static SubmitPreview PrepareSubmit(CollectionBatch batch, IReadOnlyList<CollectedProduct> products)
    {
        var productGroups = new List<ProductSubmitGroup>();

        // 只处理未放弃的、已定价的商品
        var validProducts = products
            .Where(p => !p.IsAbandoned && p.Offer != null && p.Prices != null)
            .ToList();

        foreach (var product in validProducts)
        {
            var offer = product.Offer!;
            var prices = product.Prices!;

            var requests = new List<OzonProductImportRequest>();
            var warnings = new List<SubmitWarning>();
            var priceList = new List<decimal>();

            // 使用 Zip 匹配 SKU 和 Price，只处理状态为 Priced 的 SKU
            foreach (var (sku, price) in offer.Skus.Zip(prices))
            {
                if (price.State != SkuPriceState.Priced || price.FinalPrice == null)
                    continue;

                var finalPrice = price.FinalPrice.Value;
                priceList.Add(finalPrice);

                // 生成 offer_id：{8位36进制}-{4位哈希}
                var offerId = GenerateOfferId(offer.OfferId.ToString(), sku.SkuId);

                // 组装 Ozon 请求（目前用占位数据）
                var request = BuildOzonRequest(
                    offer,
                    sku,
                    finalPrice,
                    offerId
                );

                requests.Add(request);

                // 检查数据完整性并生成警告
                ValidateAndWarn(offer, sku, offerId, warnings);
            }

            // 如果该商品有可提交的 SKU，添加到分组
            if (requests.Count > 0)
            {
                var priceRange = priceList.Count > 0
                    ? $"₽{priceList.Min():N0} - ₽{priceList.Max():N0}"
                    : "无价格";

                productGroups.Add(new ProductSubmitGroup(
                    Title: offer.Title,
                    SourceOfferId: offer.OfferId,
                    Requests: requests,
                    SkuCount: requests.Count,
                    PriceRange: priceRange,
                    Warnings: warnings
                ));
            }
        }

        var totalSkuCount = productGroups.Sum(g => g.SkuCount);

        return new SubmitPreview(
            ProductGroups: productGroups,
            TotalSkuCount: totalSkuCount,
            TotalProductCount: productGroups.Count
        );
    }

    /// <summary>
    /// 生成 Ozon offer_id：{8位36进制}-{4位哈希}。
    /// </summary>
    private static string GenerateOfferId(string sourceOfferId, string skuId)
    {
        // 前缀：源商品 ID 的 36 进制编码（8位）
        var prefix = ConvertToBase36(sourceOfferId).PadLeft(8, '0')[..8].ToUpperInvariant();

        // 后缀：SKU ID 的哈希（4位）
        var hash = ComputeHash(skuId);

        return $"{prefix}-{hash}";
    }

    /// <summary>
    /// 转换为 36 进制（0-9, A-Z）。
    /// </summary>
    private static string ConvertToBase36(string input)
    {
        if (!long.TryParse(input, out var number))
            number = Math.Abs(input.GetHashCode());

        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var result = new StringBuilder();

        do
        {
            result.Insert(0, chars[(int)(number % 36)]);
            number /= 36;
        } while (number > 0);

        return result.ToString();
    }

    /// <summary>
    /// 计算 4 位哈希后缀。
    /// </summary>
    private static string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0) % 0x10000; // 取低16位
        return value.ToString("X4"); // 4位16进制
    }

    /// <summary>
    /// 组装 Ozon 请求（目前使用占位数据）。
    /// </summary>
    private static OzonProductImportRequest BuildOzonRequest(
        CollectedOffer offer,
        CollectedSku sku,
        decimal finalPrice,
        string offerId)
    {
        // 主图：优先用 SKU 图片，否则用商品首图
        var primaryImage = sku.ImageUrl ?? offer.MainImages.FirstOrDefault();

        // 所有图片：SKU 图片 + 商品主图列表（去重）
        var allImages = new List<string>();
        if (!string.IsNullOrEmpty(sku.ImageUrl))
            allImages.Add(sku.ImageUrl);
        allImages.AddRange(offer.MainImages.Where(img => img != sku.ImageUrl));

        // 商品标题：目前用原始标题（后续接入 AI 文案后替换为俄文）
        var title = offer.Title;

        // 属性：目前用占位数据（后续接入规则引擎后生成真实属性）
        var attributes = BuildPlaceholderAttributes(offer, sku);

        return new OzonProductImportRequest(
            DescriptionCategoryId: 17029016, // 占位符：女装类目
            TypeId: 0, // 占位符：从类目Schema获取
            OfferId: offerId,
            Price: finalPrice.ToString("F2"),
            Name: title,
            Attributes: attributes,
            Images: allImages,
            PrimaryImage: primaryImage,
            CurrencyCode: "RUB",
            Vat: "0" // 默认免税
        );
    }

    /// <summary>
    /// 构建占位属性（后续接入规则引擎后替换）。
    /// </summary>
    private static IReadOnlyList<OzonAttribute> BuildPlaceholderAttributes(CollectedOffer offer, CollectedSku sku)
    {
        var attributes = new List<OzonAttribute>();

        // 如果 SKU 有规格选项，尝试生成占位属性
        // 真实实现需要查询规则引擎和属性字典
        if (sku.OptionValues.Count > 0)
        {
            // 占位：添加颜色和尺码属性（假设第一个轴是颜色，第二个是尺码）
            for (int i = 0; i < Math.Min(sku.OptionValues.Count, offer.Axes.Count); i++)
            {
                var axisName = offer.Axes[i].Name;
                var optionValue = sku.OptionValues[i];

                // 占位：假设第一个轴是颜色，第二个是尺码
                var attributeId = i == 0 ? 85L : 4389L; // 85=颜色, 4389=尺码

                attributes.Add(new OzonAttribute(
                    Id: attributeId,
                    Values: [new OzonAttributeValue(Value: optionValue)]
                ));
            }
        }

        return attributes;
    }

    /// <summary>
    /// 验证数据完整性并生成警告。
    /// </summary>
    private static void ValidateAndWarn(
        CollectedOffer offer,
        CollectedSku sku,
        string offerId,
        List<SubmitWarning> warnings)
    {
        var issues = new List<string>();

        // 检查图片
        if (string.IsNullOrEmpty(sku.ImageUrl) && offer.MainImages.Count == 0)
            issues.Add("缺少图片");

        // 检查标题长度（Ozon 限制 500 字符）
        if (offer.Title.Length > 500)
            issues.Add($"标题过长（{offer.Title.Length}/500 字符）");

        // 检查属性（目前只是占位检查）
        if (sku.OptionValues.Count == 0)
            issues.Add("缺少规格属性");

        if (issues.Count > 0)
        {
            warnings.Add(new SubmitWarning(
                OfferId: offerId,
                ProductTitle: offer.Title,
                Message: string.Join("；", issues)
            ));
        }
    }
}
