using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AutoMagic.Application.ExchangeRates;

namespace AutoMagic.Infrastructure.ExchangeRates;

public sealed class BocExchangeRateService(HttpClient httpClient) : IExchangeRateService
{
    public const string SourceName = "中国银行外汇牌价（现汇卖出价）";
    public const string LatestRatesUrl = "https://www.boc.cn/sourcedb/whpj/";

    public async Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(LatestRatesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var html = DecodeHtml(bytes, response.Content.Headers.ContentType?.CharSet);
        return BocExchangeRateParser.Parse(html);
    }

    private static string DecodeHtml(byte[] bytes, string? charset)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encodingName = string.IsNullOrWhiteSpace(charset)
            ? "utf-8"
            : charset.Trim().Trim('"');

        try
        {
            return Encoding.GetEncoding(encodingName).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}

public static partial class BocExchangeRateParser
{
    public static ExchangeRateSnapshot Parse(string html)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);

        var usd = FindRate(html, "美元");
        var rub = FindRate(html, "卢布");
        var publishedAt = usd.PublishedAt <= rub.PublishedAt
            ? usd.PublishedAt
            : rub.PublishedAt;

        return new ExchangeRateSnapshot(
            publishedAt,
            ConvertCnyToForeignCurrency(usd.SpotSellingPrice, "美元"),
            ConvertCnyToForeignCurrency(rub.SpotSellingPrice, "卢布"),
            BocExchangeRateService.SourceName);
    }

    private static BocRateRow FindRate(string html, string currencyName)
    {
        foreach (Match rowMatch in TableRowRegex().Matches(html))
        {
            var cells = TableCellRegex()
                .Matches(rowMatch.Groups["row"].Value)
                .Select(match => NormalizeCell(match.Groups["cell"].Value))
                .ToArray();

            if (cells.Length < 8 || !string.Equals(cells[0], currencyName, StringComparison.Ordinal))
            {
                continue;
            }

            var sellingPrice = ParsePositiveDecimal(cells[3], $"{currencyName}现汇卖出价");
            var publishedAt = ParsePublishedAt(cells[6], cells[7], currencyName);
            return new BocRateRow(sellingPrice, publishedAt);
        }

        throw new InvalidDataException($"中国银行牌价页面缺少{currencyName}现汇卖出价。");
    }

    private static decimal ConvertCnyToForeignCurrency(decimal cnyPerHundredForeign, string currencyName)
    {
        if (cnyPerHundredForeign <= 0)
        {
            throw new InvalidDataException($"{currencyName}现汇卖出价必须大于 0。");
        }

        return 100m / cnyPerHundredForeign;
    }

    private static decimal ParsePositiveDecimal(string value, string field)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) &&
            result > 0)
        {
            return result;
        }

        throw new InvalidDataException($"无法解析{field}：{value}。");
    }

    private static DateTime ParsePublishedAt(string date, string time, string currencyName)
    {
        var combined = $"{date.Trim()} {time.Trim()}";
        var formats = new[]
        {
            "yyyy.MM.dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
        };

        if (DateTime.TryParseExact(
                combined,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result))
        {
            return result;
        }

        throw new InvalidDataException($"无法解析{currencyName}牌价发布时间：{combined}。");
    }

    private static string NormalizeCell(string value)
    {
        var withoutTags = HtmlTagRegex().Replace(value, string.Empty);
        return WebUtility.HtmlDecode(withoutTags)
            .Replace('\u00A0', ' ')
            .Trim();
    }

    private sealed record BocRateRow(decimal SpotSellingPrice, DateTime PublishedAt);

    [GeneratedRegex(@"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<td\b[^>]*>(?<cell>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableCellRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();
}
