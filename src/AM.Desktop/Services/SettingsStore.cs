using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AM.Desktop.Services;

/// <summary>Ozon 配置。ApiKey 只在内存里是明文。</summary>
public sealed record OzonSettings(string ClientId, string ApiKey, string CurrencyCode)
{
    public bool IsComplete => ClientId.Length > 0 && ApiKey.Length > 0;
}

/// <summary>
/// 个人设置存在 %LocalAppData%\AM\settings.json。密钥用 DPAPI（当前用户）加密后存 base64，换用户或换机器无法解密。
/// </summary>
public static class SettingsStore
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AM");

    private static readonly string FilePath = Path.Combine(DataDirectory, "settings.json");
    private static readonly byte[] Entropy = "AM.Settings.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static OzonSettings LoadOzon()
    {
        var file = Read();
        return new OzonSettings(file.OzonClientId ?? "", Unprotect(file.OzonApiKey), file.CurrencyCode ?? "CNY");
    }

    public static void SaveOzon(OzonSettings settings)
    {
        var file = Read() with
        {
            OzonClientId = settings.ClientId,
            OzonApiKey = Protect(settings.ApiKey),
            CurrencyCode = settings.CurrencyCode,
        };
        Directory.CreateDirectory(DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static SettingsFile Read()
    {
        if (!File.Exists(FilePath))
        {
            return new SettingsFile();
        }

        try
        {
            return JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath), Json) ?? new SettingsFile();
        }
        catch (JsonException)
        {
            // 文件损坏按未配置处理，用户重新填写后覆盖。
            return new SettingsFile();
        }
    }

    private static string? Protect(string secret) => secret.Length == 0
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            // 换了 Windows 用户或文件被改过，解不开就当没保存。
            return "";
        }
    }

    private sealed record SettingsFile
    {
        public string? OzonClientId { get; init; }
        public string? OzonApiKey { get; init; }
        public string? CurrencyCode { get; init; }
    }
}
