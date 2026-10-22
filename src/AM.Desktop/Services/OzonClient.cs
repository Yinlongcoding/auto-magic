using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace AM.Desktop.Services;

/// <summary>Ozon Seller API 调用失败，Message 可直接显示给用户，不含密钥。</summary>
public sealed class OzonApiException(string message) : Exception(message);

/// <summary>
/// Ozon Seller API。目前只有只读的类目树接口；测试连接也用它，因为它需要有效凭据。
/// </summary>
public static class OzonClient
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api-seller.ozon.ru/"),
        // 类目树响应约 2 MB。
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>拉取类目树原始 JSON（language=ZH_HANS）。</summary>
    public static async Task<byte[]> GetCategoryTreeAsync(OzonSettings settings, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/description-category/tree");
        request.Headers.TryAddWithoutValidation("Client-Id", settings.ClientId);
        request.Headers.TryAddWithoutValidation("Api-Key", settings.ApiKey);
        request.Content = JsonContent.Create(new { language = "ZH_HANS" });

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new OzonApiException($"连不上 Ozon：{error.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OzonApiException("Ozon 请求超时。");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new OzonApiException(await DescribeFailureAsync(response, settings, cancellationToken));
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
    }

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, OzonSettings settings, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "Ozon 拒绝了凭据，请检查 Client-Id 和 API Key。";
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "Ozon 请求过于频繁，请稍后再试。";
        }

        // 响应体只截取一段，并去掉可能回显的凭据。
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim()
            .Replace(settings.ApiKey, "***", StringComparison.Ordinal)
            .Replace(settings.ClientId, "***", StringComparison.Ordinal);
        body = body.Length <= 300 ? body : body[..300] + "…";
        return $"Ozon 请求失败：HTTP {(int)response.StatusCode}。{body}";
    }
}
