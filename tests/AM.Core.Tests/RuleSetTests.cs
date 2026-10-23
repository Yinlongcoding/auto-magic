using AM.Core.Rules;

namespace AM.Core.Tests;

/// <summary>规则文件的读取、校验，以及仓库里 rules/ 目录本身能否加载。</summary>
public class RuleSetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"am-rules-{Guid.NewGuid():N}");

    public RuleSetTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, RuleSet.MappingsDirectory));
        File.WriteAllText(Path.Combine(_directory, RuleSet.AttributesFile),
            """[{ "attributeId": 10096, "name": "商品颜色", "sources": ["规格:颜色"] }]""");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteMapping(string name, string content) =>
        File.WriteAllText(Path.Combine(_directory, RuleSet.MappingsDirectory, name), content);

    [Fact]
    public void LoadsRowsWithFileName()
    {
        WriteMapping("10096.jsonl", """
            {"scope":"global","attribute":10096,"source":"粉色","valueId":61580,"label":"粉红色"}

            {"scope":"type:93211","attribute":10096,"source":"褐色","valueId":61575,"label":"棕色","note":"测试"}
            """);

        var rules = RuleSet.Load(_directory);

        Assert.Single(rules.Attributes);
        Assert.Equal(2, rules.Rows.Count);
        Assert.Equal("mappings/10096.jsonl", rules.Find("global", 10096, "粉色")!.Value.File);
    }

    [Fact]
    public void BadLineReportsFileAndLineNumber()
    {
        WriteMapping("10096.jsonl", """
            {"scope":"global","attribute":10096,"source":"粉色","valueId":61580,"label":"粉红色"}
            {"scope":"everywhere","attribute":10096,"source":"红","valueId":1,"label":"红色"}
            """);

        var error = Assert.Throws<RuleFileException>(() => RuleSet.Load(_directory));

        Assert.Contains("mappings/10096.jsonl 第 2 行", error.Message);
    }

    [Fact]
    public void SerializedRowRoundTrips()
    {
        var row = new RuleRow("type:93211", 4295, "均码", 35646, "通用", "备注");

        WriteMapping("4295.jsonl", RuleSet.Serialize(row));
        var loaded = RuleSet.Load(_directory).Rows.Single().Row;

        Assert.Equal(row, loaded);
        Assert.DoesNotContain("\\u", RuleSet.Serialize(row)); // 中文原样写，git diff 可读
    }

    [Fact]
    public void ValidatorFlagsWrongIdAndLabel()
    {
        var rules = new RuleSet([], new[]
        {
            (new RuleRow("type:93211", 4295, "S", 35545, "42"), "a"),
            (new RuleRow("type:93211", 4295, "M", 99999, "44"), "a"),
            (new RuleRow("type:93211", 4295, "L", 35545, "46"), "a"),
            (new RuleRow("type:93216", 4295, "XL", 99999, "48"), "a"), // 别的类型，不校验
            (new RuleRow("global", 31, RuleRow.Missing, 126745801, "Нет бренда"), "b"),
        });
        var dictionaries = new Dictionary<long, IReadOnlyList<DictionaryEntry>> { [4295] = [new(35545, "42")] };

        var result = RuleValidator.Validate(rules, 200000933, 93211, new HashSet<long> { 4295, 31 }, dictionaries);

        Assert.Equal(["M", "L"], result.Errors.Select(e => e.Row.Source));
        Assert.Equal(31, Assert.Single(result.Unverified).Row.Attribute);
    }

    [Fact]
    public void RepositoryRulesLoad()
    {
        // 仓库里的规则文件必须能加载：格式错误或重复行在这里就失败，不等到运行时
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "rules", RuleSet.AttributesFile)))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        var rules = RuleSet.Load(Path.Combine(directory, "rules"));
        Assert.NotEmpty(rules.Attributes);
        Assert.All(rules.Rows, r => Assert.Contains(rules.Attributes, a => a.AttributeId == r.Row.Attribute));
    }
}
