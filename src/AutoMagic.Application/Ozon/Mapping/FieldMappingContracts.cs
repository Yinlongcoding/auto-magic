namespace AutoMagic.Application.Ozon.Mapping;

public sealed record TypeFieldPlan(int SchemaVersion, long DescriptionCategoryId, long TypeId,
    IReadOnlyList<FieldModuleReference> Attributes);
public sealed record FieldModuleReference(long Id, string Path);
public sealed record FieldRuleModule(int SchemaVersion, string ModuleId, int Revision, string Status,
    long? DescriptionCategoryId, long? TypeId, long DictionaryId, FieldBinding Binding,
    IReadOnlyList<DictionaryValueMapping> Values, string? ValuePath = null, string TargetFingerprint = "*");
public sealed record ValueDictionaryModule(int SchemaVersion, string ModuleId, int Revision, string Status,
    long AttributeId, long DictionaryId, long? DescriptionCategoryId, long? TypeId,
    IReadOnlyList<DictionaryValueMapping> Values, string? ValuePath = null);
public sealed record ValueModuleTrace(string Path, int Revision, string ContentFingerprint);
public sealed record DictionaryValueMapping(string MappingId, int Revision, string Status,
    string DisplayZh, string TargetValue, long ValueId);
public sealed record LoadedFieldModule(FieldModuleReference Reference, FieldRuleModule? Module, string? Issue,
    string? ValueIssue = null, IReadOnlyList<ValueModuleTrace>? ValueTrace = null);
public sealed record FieldMappingBundle(TypeFieldPlan Plan, IReadOnlyList<LoadedFieldModule> Fields);
public sealed record ReviewCount(int Checked, int Correct)
{
    public bool Trusted => Checked >= 100 && Correct * 100L >= Checked * 98L;
    public bool LowConfidence => Checked >= 100 && !Trusted;
}
public interface IFieldMappingStore
{
    string RootPath { get; }
    FieldMappingBundle Load(long categoryId, long typeId);
    ReviewCount Counts(string key);
}
