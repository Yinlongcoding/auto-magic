namespace AM.Core.Rules;

/// <summary>
/// 属性声明（18 号"很小的声明文件"）：这个 Ozon 属性从哪些 1688 字段取值，按顺序取第一个有值的。
/// 字段写成"规格:颜色"（SKU 级）或"参数:品牌"（商品级）。Sources 为空表示不看源数据，
/// 永远按 <c>&lt;缺失&gt;</c> 查规则，用于品牌、性别这类按层写死的值。
/// </summary>
/// <param name="PrefixMatch">
/// 前三档都没命中时，取规则源值或字典中文名里能作为源值开头的最长一个（至少 2 个字，不含纯数字）。
/// 用于颜色"杏色大花""白色玫瑰碎花"这类"颜色 + 花型说明"。是模糊匹配，只给需要的属性打开，报告里单列供复查。
/// </param>
public sealed record AttributeDeclaration(long AttributeId, string Name, IReadOnlyList<string> Sources, bool PrefixMatch = false);

/// <summary>
/// 一行规则：(层, 属性, 源值) → valueId。Label 是字典里的名称，只用于阅读和校验，匹配不看它。
/// 层写成 global、category:类目ID、type:类型ID。
/// </summary>
public sealed record RuleRow(string Scope, long Attribute, string Source, long ValueId, string Label, string? Note = null)
{
    /// <summary>源值缺失时的兜底行用这个源值（18 号）。</summary>
    public const string Missing = "<缺失>";
}

/// <summary>字典里的一个值。只给整本缓存的字典；大字典不在这里，匹配不了就靠规则。</summary>
public sealed record DictionaryEntry(long Id, string Value);

/// <summary>人工修订：本任务内对某个源值选定的结果（18 号"同一任务内复用"）。按原始源值整体匹配。</summary>
public sealed record ManualEdit(long AttributeId, string Source, long ValueId, string Label);

/// <summary>命中的层，按优先级从高到低。</summary>
public enum MatchLayer
{
    Manual,
    Type,
    Category,
    Global,

    /// <summary>规整后的源值和字典里唯一的中文名完全相同，不需要规则。</summary>
    Dictionary,

    None,
}

/// <summary>一个 SKU 一个属性的匹配结果，含追溯信息（18 号方案 C）。</summary>
/// <param name="SourceValue">原始源值，缺失为 null。</param>
/// <param name="MatchedText">实际命中的规整后文本，如源值"1绿色有现货"命中的是"绿色"。</param>
/// <param name="RuleFile">命中规则的文件，相对规则目录；命中字典或人工修订时为 null。</param>
/// <param name="Failure">没匹配上的原因，匹配上为 null。</param>
public sealed record AttributeMatch(
    long AttributeId,
    string? SourceValue,
    string? MatchedText,
    long? ValueId,
    string? Label,
    MatchLayer Layer,
    string? RuleFile,
    string? Failure)
{
    public bool IsMatched => ValueId is not null;
}

/// <summary>
/// 一个 SKU 的源数据。键是"规格:颜色"、"参数:品牌"这样的字段名，值是原文。
/// 由桌面端从采集结果转换而来，Core 不依赖采集协议。
/// </summary>
public sealed record SkuSource(IReadOnlyDictionary<string, string> Fields)
{
    public string? Get(string field) => Fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>规则文件格式或内容有误。Message 指明文件和行号。</summary>
public sealed class RuleFileException(string message) : Exception(message);
