using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace AutoMagic.Application.Ozon.Mapping;

public static class ProductMappingJson
{
    public static JsonSerializerOptions StrictOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static JsonSerializerOptions IndentedOptions { get; } = new(StrictOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}
