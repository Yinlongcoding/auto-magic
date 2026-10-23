using System.Text.Encodings.Web;
using System.Text.Json;

namespace AM.Core.Rules;

/// <summary>
/// 规则目录的内容（18 号方案 C，按行存储）：
/// <code>
/// rules/
///   attributes.json            属性声明，很少改
///   mappings/{属性ID}.jsonl     规则行，一行一条，按 (层, 源值) 排序，表单只追加
/// </code>
/// 每个属性一个文件，改动时 git diff 只落在那个属性上。
/// </summary>
public sealed class RuleSet
{
    public const string AttributesFile = "attributes.json";
    public const string MappingsDirectory = "mappings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // (层, 属性, 规整后的源值) → (规则, 文件)
    private readonly Dictionary<(string Scope, long Attribute, string Source), (RuleRow Row, string File)> _index;

    public RuleSet(IReadOnlyList<AttributeDeclaration> attributes, IEnumerable<(RuleRow Row, string File)> rows)
    {
        Attributes = attributes;
        _index = [];
        var all = new List<(RuleRow, string)>();
        foreach (var (row, file) in rows)
        {
            var key = (row.Scope, row.Attribute, SourceNormalizer.Normalize(row.Source));
            if (_index.TryGetValue(key, out var existing))
            {
                // 同一层同一源值只能有一个结果，否则命中哪条取决于读文件的顺序
                throw new RuleFileException(
                    $"规则重复：{file} 和 {existing.File} 都定义了 {row.Scope} 属性 {row.Attribute} 源值\"{row.Source}\"。");
            }

            _index[key] = (row, file);
            all.Add((row, file));
        }

        Rows = all;
    }

    public IReadOnlyList<AttributeDeclaration> Attributes { get; }

    /// <summary>全部规则行和所在文件，校验和报告用。</summary>
    public IReadOnlyList<(RuleRow Row, string File)> Rows { get; }

    /// <summary>某属性在这些层里的全部规整后源值，前缀匹配用。</summary>
    public IEnumerable<string> SourcesIn(long attributeId, IReadOnlyCollection<string> scopes) =>
        _index.Keys.Where(k => k.Attribute == attributeId && scopes.Contains(k.Scope)).Select(k => k.Source);

    public (RuleRow Row, string File)? Find(string scope, long attributeId, string normalizedSource) =>
        _index.TryGetValue((scope, attributeId, normalizedSource), out var hit) ? hit : null;

    public static string TypeScope(long typeId) => $"type:{typeId}";

    public static string CategoryScope(long descriptionCategoryId) => $"category:{descriptionCategoryId}";

    public const string GlobalScope = "global";

    /// <summary>从规则目录加载。格式错误抛 <see cref="RuleFileException"/>，指明文件和行号。</summary>
    public static RuleSet Load(string directory)
    {
        var attributesPath = Path.Combine(directory, AttributesFile);
        if (!File.Exists(attributesPath))
        {
            throw new RuleFileException($"找不到属性声明 {attributesPath}。");
        }

        List<AttributeDeclaration> attributes;
        try
        {
            attributes = JsonSerializer.Deserialize<List<AttributeDeclaration>>(File.ReadAllText(attributesPath), JsonOptions) ?? [];
        }
        catch (JsonException error)
        {
            throw new RuleFileException($"{AttributesFile} 格式错误：{error.Message}");
        }

        var rows = new List<(RuleRow, string)>();
        var mappings = Path.Combine(directory, MappingsDirectory);
        if (Directory.Exists(mappings))
        {
            foreach (var path in Directory.GetFiles(mappings, "*.jsonl").Order(StringComparer.Ordinal))
            {
                var file = $"{MappingsDirectory}/{Path.GetFileName(path)}";
                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    rows.Add((ParseRow(line, file, lineNumber), file));
                }
            }
        }

        return new RuleSet(attributes, rows);
    }

    private static RuleRow ParseRow(string line, string file, int lineNumber)
    {
        RuleRow? row;
        try
        {
            row = JsonSerializer.Deserialize<RuleRow>(line, JsonOptions);
        }
        catch (JsonException error)
        {
            throw new RuleFileException($"{file} 第 {lineNumber} 行格式错误：{error.Message}");
        }

        if (row is null || string.IsNullOrWhiteSpace(row.Scope) || string.IsNullOrWhiteSpace(row.Source) || row.ValueId <= 0)
        {
            throw new RuleFileException($"{file} 第 {lineNumber} 行缺少 scope、source 或 valueId。");
        }

        if (row.Scope != GlobalScope && !row.Scope.StartsWith("category:", StringComparison.Ordinal)
                                     && !row.Scope.StartsWith("type:", StringComparison.Ordinal))
        {
            throw new RuleFileException($"{file} 第 {lineNumber} 行的层\"{row.Scope}\"无效，应为 global、category:ID 或 type:ID。");
        }

        return row;
    }

    /// <summary>规则行序列化成一行，写规则文件用（开发版表单）。</summary>
    public static string Serialize(RuleRow row) => JsonSerializer.Serialize(row, JsonOptions);
}
