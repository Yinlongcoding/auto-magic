using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoMagic.Application.Ozon.Mapping;

/// <summary>One explicit type plan drives both source bindings and confirmed value matching.</summary>
public sealed class FieldMappingService(IFieldMappingStore store)
{
    public string RulesPath => store.RootPath;
    public IReadOnlyList<FieldBindingRow> CreateRequired(FieldMatchingInput source)
    {
        var request = ProductMappingInputBuilder.Create(source).Request;
        var bundle = store.Load(request.DescriptionCategoryId, request.TypeId);
        var usable = bundle.Fields.Where(f => f.Module is not null && f.Issue is null &&
            TargetMatches(f.Module, request.Attributes.SingleOrDefault(a => a.AttributeId == f.Reference.Id))).ToArray();
        var rows = FieldBindingPreview.CreateRequired(source, usable.Select(f => f.Module!.Binding).ToArray());
        foreach (var row in rows)
        {
            var field = bundle.Fields.SingleOrDefault(f => f.Reference.Id == row.AttributeId);
            row.ModulePath = field?.Reference.Path;
            if (field?.Module is not null && field.Issue is null &&
                TargetMatches(field.Module, request.Attributes.SingleOrDefault(a => a.AttributeId == row.AttributeId)))
                row.BindingFingerprint = BindingFingerprint(field.Module);
            else
                row.Status = field?.Issue ?? (field is null ? "类型方案未引用该必填属性，待填写" : "模块与当前 Schema 不匹配，待填写");
        }
        return rows;
    }

    public void ResolveConfirmedFields(FieldMatchingInput source, IReadOnlyList<FieldBindingRow> rows)
    {
        foreach (var row in rows) row.ClearValueMatch();
        var request = ProductMappingInputBuilder.Create(source).Request;
        var bundle = store.Load(request.DescriptionCategoryId, request.TypeId);
        foreach (var row in rows)
        {
            var field = bundle.Fields.SingleOrDefault(f => f.Reference.Id == row.AttributeId);
            var fingerprint = field?.Module is not null && field.Issue is null &&
                TargetMatches(field.Module, request.Attributes.SingleOrDefault(a => a.AttributeId == row.AttributeId))
                ? BindingFingerprint(field.Module) : null;
            if (row.ModulePath != field?.Reference.Path || row.BindingFingerprint != fingerprint)
            {
                row.Reviewed = false;
                row.ValueStatus = "引用或字段规则已变化，请重新生成方案";
                continue;
            }
            ConfirmedFieldValueResolver.Resolve(request, row, field, store.Counts);
        }
    }

    internal static bool Active(string status) => status is "active" or "trusted";
    internal static bool TargetMatches(FieldRuleModule module, ProductMappingAttribute? attribute) =>
        attribute is not null && attribute.AttributeId == module.Binding.Id &&
        attribute.DictionaryId == module.DictionaryId && attribute.AttributeComplexId == 0 &&
        (module.TargetFingerprint == "*" || module.TargetFingerprint == SchemaFingerprint(attribute));
    internal static string BindingFingerprint(FieldRuleModule module) => Hash(JsonSerializer.Serialize(new {
        module.Binding, module.DictionaryId, module.DescriptionCategoryId, module.TypeId, module.TargetFingerprint
    }));
    public static string BindingKey(ProductMappingRequest request, LoadedFieldModule field) =>
        $"explicit/{request.DescriptionCategoryId}/{request.TypeId}/{field.Reference.Path}/{field.Module!.Revision}/" +
        Hash(JsonSerializer.Serialize(new { Binding = BindingFingerprint(field.Module), Attribute =
            request.Attributes.Single(a => a.AttributeId == field.Reference.Id) with { DictionaryCandidates = [] } }));
    public static string ValueKey(ProductMappingRequest request, LoadedFieldModule field, DictionaryValueMapping value) =>
        BindingKey(request, field) + $"/{value.MappingId}/{value.Revision}/" +
        Hash(JsonSerializer.Serialize(new { value, field.ValueTrace }));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string SchemaFingerprint(ProductMappingAttribute attribute) => Hash(JsonSerializer.Serialize(new {
        attribute.AttributeId, attribute.AttributeComplexId, attribute.Type, attribute.IsCollection,
        attribute.MaxValueCount, attribute.DictionaryId, attribute.IsRequired
    }));
    internal static bool ValidScalar(string type, string value) => type.Trim().ToLowerInvariant() switch {
        "string" or "text" => !string.IsNullOrWhiteSpace(value),
        "integer" or "int" or "int32" or "int64" => long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _),
        "decimal" or "number" or "float" or "double" => decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
        "boolean" or "bool" => value is "true" or "false",
        _ => false,
    };
}
