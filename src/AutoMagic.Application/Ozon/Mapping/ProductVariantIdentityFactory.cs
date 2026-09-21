using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoMagic.Application.Ozon.Mapping;

public sealed record ProductVariantIdentity(
    string ProductGroupKey,
    string VariantKey,
    string MerchantSku,
    string IdentityStrategy);

/// <summary>
/// Creates deterministic internal and merchant-facing identifiers without
/// treating an option-axis cartesian product as observed source SKUs.
/// </summary>
public static class ProductVariantIdentityFactory
{
    public const int MerchantSkuMaximumLength = 50;

    public static ProductVariantIdentity Create(
        string sourceOfferId,
        string? sourceSkuId,
        IReadOnlyDictionary<string, string?> options,
        IReadOnlyDictionary<string, string?>? optionIds = null,
        IReadOnlyDictionary<string, string?>? dimensionIds = null)
    {
        if (string.IsNullOrWhiteSpace(sourceOfferId))
            throw new ArgumentException("源商品标识不能为空。", nameof(sourceOfferId));
        ArgumentNullException.ThrowIfNull(options);

        var offerId = sourceOfferId.Trim();
        var hasCompleteOptionIds = optionIds is not null && options.Count > 0 &&
            options.Keys.All(name => optionIds.TryGetValue(name, out var id) && !string.IsNullOrWhiteSpace(id));
        var identityStrategy = !string.IsNullOrWhiteSpace(sourceSkuId)
            ? "source-sku-id"
            : hasCompleteOptionIds ? "source-option-ids" : "source-option-values";
        var sourceIdentity = identityStrategy switch
        {
            "source-sku-id" => $"source-sku:{sourceSkuId!.Trim()}",
            "source-option-ids" => $"option-ids:{CanonicalOptionIds(options, optionIds!, dimensionIds)}",
            _ => $"option-values:{CanonicalOptions(options)}",
        };
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"1688\n{offerId}\n{sourceIdentity}")));
        var variantKey = $"variant:1688:{digest[..20]}";
        var offerToken = SanitizeToken(offerId);
        var suffix = digest[..12].ToUpperInvariant();
        const string prefix = "AM-1688-";
        var maximumOfferLength = MerchantSkuMaximumLength - prefix.Length - 1 - suffix.Length;
        if (offerToken.Length > maximumOfferLength) offerToken = offerToken[..maximumOfferLength];
        var merchantSku = $"{prefix}{offerToken}-{suffix}";
        return new($"1688:{offerId}", variantKey, merchantSku, identityStrategy);
    }

    private static string CanonicalOptions(IReadOnlyDictionary<string, string?> options)
    {
        var values = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Key) && !string.IsNullOrWhiteSpace(option.Value))
            .Select(option => (Name: option.Key.Trim(), Value: CanonicalIdentityValue(option.Key, option.Value!)))
            .OrderBy(option => option.Name, StringComparer.Ordinal)
            .ThenBy(option => option.Value, StringComparer.Ordinal)
            .Select(option => $"{option.Name.Length}:{option.Name}={option.Value.Length}:{option.Value}")
            .ToArray();
        if (values.Length == 0) throw new ArgumentException("SKU 至少需要一个有效规格值。", nameof(options));
        return string.Join("|", values);
    }

    private static string CanonicalIdentityValue(string name, string value)
    {
        var normalized = Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(),
            @"\s*(?:有现货|现货|有货|缺货|无货|售罄|库存\s*\d+\s*(?:件|个)?)\s*$", string.Empty,
            RegexOptions.CultureInvariant).Trim();
        if (Regex.IsMatch(name, @"颜色|色彩|colou?r", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var numbered = Regex.Match(normalized, @"^\d+\s*(.+)$", RegexOptions.CultureInvariant);
            if (numbered.Success && Regex.IsMatch(numbered.Groups[1].Value,
                @"黑|白|灰|红|粉|橙|黄|绿|青|蓝|紫|棕|褐|咖啡|卡其|杏|驼|米|藏青|墨绿|酒红|玫红|香槟|肤|银|金|彩色|透明|black|white|gr[ae]y|red|pink|orange|yellow|green|blue|purple|brown|gold|silver",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                normalized = numbered.Groups[1].Value.Trim();
        }
        return normalized;
    }

    private static string CanonicalOptionIds(
        IReadOnlyDictionary<string, string?> options,
        IReadOnlyDictionary<string, string?> optionIds,
        IReadOnlyDictionary<string, string?>? dimensionIds) =>
        string.Join("|", options.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                var dimension = dimensionIds is not null && dimensionIds.TryGetValue(name, out var id) &&
                    !string.IsNullOrWhiteSpace(id) ? id!.Trim() : name.Trim();
                return $"{dimension.Length}:{dimension}={optionIds[name]!.Trim()}";
            }));

    private static string SanitizeToken(string value)
    {
        var token = new string(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? char.ToUpperInvariant(character)
                : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(token) ? "OFFER" : token;
    }
}
