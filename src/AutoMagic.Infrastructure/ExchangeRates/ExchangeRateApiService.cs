using System.Globalization;
using System.Text.Json;
using AutoMagic.Application.ExchangeRates;

namespace AutoMagic.Infrastructure.ExchangeRates;

public sealed class ExchangeRateApiService(HttpClient httpClient) : IExchangeRateService
{
    public const string SourceName = "ExchangeRate-API 开放参考汇率 · exchangerate-api.com";
    public const string LatestRatesUrl = "https://open.er-api.com/v6/latest/CNY";

    public async Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(LatestRatesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExchangeRateApiParser.Parse(json);
    }
}

public static class ExchangeRateApiParser
{
    public static ExchangeRateSnapshot Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("result", out var result) ||
            !string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            var errorType = root.TryGetProperty("error-type", out var error)
                ? error.GetString()
                : null;
            throw new InvalidDataException(
                $"汇率接口返回失败{(string.IsNullOrWhiteSpace(errorType) ? "。" : $"：{errorType}。")}");
        }

        if (!root.TryGetProperty("base_code", out var baseCode) ||
            !string.Equals(baseCode.GetString(), "CNY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("汇率接口返回的基准货币不是 CNY。");
        }

        if (!root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("汇率接口响应缺少 rates 数据。");
        }

        var cnyToUsd = ReadPositiveRate(rates, "USD");
        var cnyToRub = ReadPositiveRate(rates, "RUB");
        var publishedAt = ReadPublishedAt(root);

        return new ExchangeRateSnapshot(
            publishedAt,
            cnyToUsd,
            cnyToRub,
            ExchangeRateApiService.SourceName);
    }

    private static decimal ReadPositiveRate(JsonElement rates, string currencyCode)
    {
        if (!rates.TryGetProperty(currencyCode, out var rate) ||
            !rate.TryGetDecimal(out var value) ||
            value <= 0)
        {
            throw new InvalidDataException($"汇率接口响应缺少有效的 CNY/{currencyCode} 汇率。");
        }

        return value;
    }

    private static DateTime ReadPublishedAt(JsonElement root)
    {
        if (root.TryGetProperty("time_last_update_unix", out var unixTime) &&
            unixTime.TryGetInt64(out var seconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                // 继续尝试接口提供的 UTC 文本。
            }
        }

        if (root.TryGetProperty("time_last_update_utc", out var utcText) &&
            DateTimeOffset.TryParse(
                utcText.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var publishedAt))
        {
            return publishedAt.ToLocalTime().DateTime;
        }

        throw new InvalidDataException("汇率接口响应缺少有效的更新时间。");
    }
}
