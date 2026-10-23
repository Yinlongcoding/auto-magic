using System.IO;
using AM.Bridge;
using AM.Core.Rules;

namespace AM.Desktop.Services;

/// <summary>
/// 规则目录的位置和采集数据到规则引擎输入的转换。
/// 开发版用仓库里的 rules/（从程序目录往上找），改动直接进 git（18 号）。
/// 发布版随应用打包的位置待定（18 号"具体位置待定"），现在找不到就报错。
/// </summary>
public static class RuleStore
{
    public static string? FindDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "rules");
            if (File.Exists(Path.Combine(candidate, RuleSet.AttributesFile)))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>加载规则。找不到目录或文件有误抛 <see cref="RuleFileException"/>。</summary>
    public static RuleSet Load() =>
        RuleSet.Load(FindDirectory() ?? throw new RuleFileException("找不到规则目录 rules/。"));

    /// <summary>规格按 SKU 取值（"规格:颜色"），参数是商品级的整段原文（"参数:品牌"）。</summary>
    public static SkuSource SourceFor(CollectedOffer offer, CollectedSku sku)
    {
        var fields = new Dictionary<string, string>();
        foreach (var attribute in offer.Attributes)
        {
            fields.TryAdd($"参数:{attribute.Label}", attribute.Value);
        }

        for (var i = 0; i < offer.Axes.Count && i < sku.OptionValues.Count; i++)
        {
            fields[$"规格:{offer.Axes[i].Name}"] = sku.OptionValues[i];
        }

        return new SkuSource(fields);
    }

    /// <summary>整本缓存的字典，转成引擎用的形式。按需搜索的大字典不在里面。</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<DictionaryEntry>> DictionariesFor(TypeSchema schema) =>
        schema.Dictionaries
            .Where(d => d.Full)
            .ToDictionary(d => d.AttributeId,
                d => (IReadOnlyList<DictionaryEntry>)d.Values.Select(v => new DictionaryEntry(v.Id, v.Value)).ToList());
}
