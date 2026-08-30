using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoMagic.Contracts.Protocol;

public static class BridgeJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static JsonSerializerOptions IndentedOptions { get; } = new(Options)
    {
        WriteIndented = true,
    };
}
