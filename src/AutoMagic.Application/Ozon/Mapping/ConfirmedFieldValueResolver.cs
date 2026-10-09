namespace AutoMagic.Application.Ozon.Mapping;

/// <summary>Matches only the module explicitly referenced by the type; no inheritance, searches or fallback.</summary>
public static class ConfirmedFieldValueResolver
{
    public static void Resolve(ProductMappingRequest request, FieldBindingRow row, LoadedFieldModule? field,
        Func<string, ReviewCount> counts)
    {
        row.ClearValueMatch();
        if (!row.Reviewed) return;
        row.ValueSourcePaths = field?.ValueTrace?.Select(t => t.Path).ToArray() ?? [];
        if (field?.Module is null || field.Issue is not null)
        { row.ValueStatus = field?.Issue ?? "类型方案未引用该属性，留空"; return; }
        var module = field.Module;
        if (field.ValueIssue is not null) { row.ValueStatus = field.ValueIssue; return; }
        var attribute = request.Attributes.SingleOrDefault(a => a.AttributeId == row.AttributeId);
        if (!row.SchemaValid || row.AttributeId != field.Reference.Id || attribute is null ||
            attribute.DictionaryId != row.DictionaryId || !FieldMappingService.TargetMatches(module, attribute))
        { row.ValueStatus = "模块与当前 Schema 不匹配，留空"; return; }
        if (!FieldMappingService.Active(module.Status) || counts(FieldMappingService.BindingKey(request, field)).LowConfidence)
        { row.ValueStatus = "模块未启用或低可信，留空"; return; }
        var text = row.InputText.Trim();
        if (text.Length == 0) { row.ValueStatus = "确认内容为空，留空"; return; }
        if (attribute.DictionaryId == 0)
        {
            if (!FieldMappingService.ValidScalar(attribute.Type, text))
            { row.ValueStatus = "值类型不符，留空"; return; }
            row.TargetValue = text; row.ValueResolution = "direct";
            row.ValueStatus = "普通字段，无需 valueId";
            return;
        }
        var matches = module.Values.Where(v => v.DisplayZh == text).ToArray();
        if (matches.Length == 0) { row.ValueStatus = "中文未命中引用模块，留空"; return; }
        if (matches.Select(v => (v.ValueId, v.TargetValue)).Distinct().Count() != 1)
        { row.ValueStatus = "中文对应多个目标，留空"; return; }
        if (matches.Any(v => !FieldMappingService.Active(v.Status) ||
            counts(FieldMappingService.ValueKey(request, field, v)).LowConfidence))
        { row.ValueStatus = "值映射未启用或低可信，留空"; return; }
        row.ValueId = matches[0].ValueId; row.TargetValue = matches[0].TargetValue;
        row.ValueResolution = "json"; row.ValueStatus = "已命中引用模块";
    }
}
