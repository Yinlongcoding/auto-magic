using System.IO;
using System.Text;
using AM.Bridge;
using AM.Core.Rules;
using AM.Desktop.Models;

namespace AM.Desktop.Services;

/// <summary>
/// 开发用：属性覆盖报告。对照一个批次的 1688 数据和该类型的 Ozon 属性、字典，回答
/// "要映射哪些属性、1688 哪个字段对得上、直接命中多少、剩下的长什么样"，用来决定规则怎么写。
/// 不做任何映射决定，只统计。
/// </summary>
public static class AttributeReport
{
    /// <summary>每个字典属性列出的候选源字段数。</summary>
    private const int CandidateCount = 3;

    /// <summary>未命中值最多列出的个数。</summary>
    private const int UnmatchedSampleCount = 40;

    /// <param name="rules">规则；加载失败时为 null，原因在 rulesError。</param>
    public static string Write(CollectionBatch batch, IReadOnlyList<CollectedProduct> products, TypeSchema schema,
        RuleSet? rules, string? rulesError)
    {
        var offers = products.Select(p => p.Offer).OfType<CollectedOffer>().DistinctBy(o => o.OfferId).ToList();
        var fields = CollectSourceFields(offers);

        var md = new StringBuilder();
        md.AppendLine($"# 属性覆盖报告：{batch.Keyword}");
        md.AppendLine();
        md.AppendLine($"- 批次 {batch.Id}，类型 {batch.CategoryPath}（类目 {schema.DescriptionCategoryId}，类型 {schema.TypeId}）");
        md.AppendLine($"- 商品 {offers.Count} 个，SKU {offers.Sum(o => o.Skus.Count)} 个");
        md.AppendLine($"- 属性和字典拉取于 {schema.FetchedAt:yyyy-MM-dd HH:mm}，报告生成于 {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine("- 命中只做两种比较：完全相同；源值包含字典值（字典值至少 2 个字）。都不是规则，只是看规模。");
        md.AppendLine();

        WriteRuleMatches(md, batch, offers, schema, rules, rulesError);
        WriteAttributeTable(md, schema);
        WriteDictionaryMatches(md, schema, fields);
        WriteSourceFields(md, fields, offers.Count);

        var directory = Path.Combine(SettingsStore.DataDirectory, "reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{batch.Id}-attributes.md");
        File.WriteAllText(path, md.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>用当前规则跑一遍本批次全部 SKU：每个属性命中多少、命中在哪层、哪些源值没匹配上。</summary>
    private static void WriteRuleMatches(StringBuilder md, CollectionBatch batch, IReadOnlyList<CollectedOffer> offers,
        TypeSchema schema, RuleSet? rules, string? rulesError)
    {
        md.AppendLine("## 一、规则命中");
        md.AppendLine();
        if (rules is null)
        {
            md.AppendLine($"规则没有加载：{rulesError}");
            md.AppendLine();
            return;
        }

        var dictionaries = RuleStore.DictionariesFor(schema);
        var validation = RuleValidator.Validate(rules, schema.DescriptionCategoryId, schema.TypeId,
            schema.Attributes.Select(a => a.Id).ToHashSet(), dictionaries);
        if (validation.Errors.Count > 0)
        {
            md.AppendLine("**规则校验错误**（valueId 或名称和该类型的字典对不上）：");
            md.AppendLine();
            foreach (var issue in validation.Errors)
            {
                md.AppendLine($"- {issue.File}：{issue.Row.Scope} 源值\"{issue.Row.Source}\"：{issue.Problem}");
            }

            md.AppendLine();
        }

        if (validation.Unverified.Count > 0)
        {
            md.AppendLine("没有整本字典、无法离线核验的规则：" +
                          string.Join("；", validation.Unverified.Select(u => $"{u.Row.Attribute} {u.Row.Source}→{u.Row.Label}({u.Row.ValueId})")));
            md.AppendLine();
        }

        var matches = offers
            .SelectMany(o => o.Skus.SelectMany(sku => RuleEngine.Resolve(rules, batch.DescriptionCategoryId, batch.TypeId,
                RuleStore.SourceFor(o, sku), dictionaries, [])))
            .ToList();
        var skuCount = offers.Sum(o => o.Skus.Count);

        md.AppendLine($"按 rules/ 当前内容跑本批次 {skuCount} 个 SKU。层的含义：类型、类目、全局是规则文件；字典是源值和字典里唯一的中文名相同。");
        md.AppendLine();
        md.AppendLine("| 属性 | 命中 SKU | 类型层 | 类目层 | 全局层 | 字典 | 未匹配 SKU | 未匹配去重源值 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var declaration in rules.Attributes)
        {
            var mine = matches.Where(m => m.AttributeId == declaration.AttributeId).ToList();
            int Count(MatchLayer layer) => mine.Count(m => m.Layer == layer);
            var unmatched = mine.Where(m => !m.IsMatched).ToList();
            md.AppendLine($"| {declaration.AttributeId} {declaration.Name} | {mine.Count(m => m.IsMatched)}/{mine.Count} | " +
                          $"{Count(MatchLayer.Type)} | {Count(MatchLayer.Category)} | {Count(MatchLayer.Global)} | {Count(MatchLayer.Dictionary)} | " +
                          $"{unmatched.Count} | {unmatched.Select(m => m.SourceValue).Distinct().Count()} |");
        }

        md.AppendLine();

        foreach (var declaration in rules.Attributes)
        {
            var unmatched = matches.Where(m => m.AttributeId == declaration.AttributeId && !m.IsMatched)
                .GroupBy(m => (m.SourceValue, m.Failure))
                .OrderByDescending(g => g.Count())
                .ToList();
            if (unmatched.Count == 0)
            {
                continue;
            }

            md.AppendLine($"### {declaration.AttributeId} {declaration.Name}：未匹配的源值");
            md.AppendLine();
            md.AppendLine("| 源值 | SKU 数 | 原因 |");
            md.AppendLine("|---|---|---|");
            foreach (var group in unmatched)
            {
                md.AppendLine($"| {Escape(group.Key.SourceValue ?? "（缺失）")} | {group.Count()} | {Escape(group.Key.Failure ?? "")} |");
            }

            md.AppendLine();
        }

        // 被剥掉修饰才命中的值，供检查规整有没有剥错
        var stripped = matches
            .Where(m => m.IsMatched && m.SourceValue is not null && m.MatchedText != SourceNormalizer.Normalize(m.SourceValue))
            .GroupBy(m => (m.AttributeId, m.SourceValue, m.MatchedText, m.Label))
            .Select(g => $"{g.Key.SourceValue} → {g.Key.MatchedText} → {g.Key.Label}")
            .ToList();
        if (stripped.Count > 0)
        {
            md.AppendLine("### 去修饰或拆分后才命中的源值（检查有没有剥错）");
            md.AppendLine();
            foreach (var line in stripped)
            {
                md.AppendLine($"- {line}");
            }

            md.AppendLine();
        }
    }

    private static void WriteAttributeTable(StringBuilder md, TypeSchema schema)
    {
        md.AppendLine("## 二、Ozon 属性");
        md.AppendLine();
        md.AppendLine($"共 {schema.Attributes.Count} 个，必填 {schema.Attributes.Count(a => a.IsRequired)} 个，" +
                      $"变体 {schema.Attributes.Count(a => a.IsAspect)} 个。只列必填和变体；其余是可选属性，第一阶段留空（12 号）。");
        md.AppendLine();
        md.AppendLine("| ID | 名称 | 必填 | 变体 | 多值 | 字典 | 字典规模 |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var a in schema.Attributes.Where(a => a.IsRequired || a.IsAspect)
                     .OrderByDescending(a => a.IsRequired).ThenByDescending(a => a.IsAspect))
        {
            var size = a.DictionaryId == 0 ? "无字典（文本）"
                : schema.DictionaryFor(a.Id) is { Full: true } d ? $"{d.Values.Count} 条"
                : "太大，按需搜索";
            md.AppendLine($"| {a.Id} | {a.Name} | {Mark(a.IsRequired)} | {Mark(a.IsAspect)} | {Mark(a.IsCollection)} | " +
                          $"{(a.DictionaryId == 0 ? "" : a.DictionaryId)} | {size} |");
        }

        md.AppendLine();
    }

    private static void WriteDictionaryMatches(StringBuilder md, TypeSchema schema, IReadOnlyList<SourceField> fields)
    {
        md.AppendLine("## 三、字典属性与 1688 字段");
        md.AppendLine();
        foreach (var dictionary in schema.Dictionaries)
        {
            var attribute = schema.Attributes.First(a => a.Id == dictionary.AttributeId);
            md.AppendLine($"### {attribute.Id} {attribute.Name}");
            md.AppendLine();

            if (!dictionary.Full)
            {
                md.AppendLine("字典太大，没有整本缓存，无法离线比对。名称相近的 1688 字段：");
                md.AppendLine();
                foreach (var field in fields.Where(f => NameLooksRelated(f.Name, attribute.Name)))
                {
                    md.AppendLine($"- {field.Label}：{string.Join("、", field.Values.Take(UnmatchedSampleCount))}");
                }

                md.AppendLine();
                continue;
            }

            var names = dictionary.Values.Select(v => v.Value).ToHashSet();
            var duplicates = dictionary.Values.GroupBy(v => v.Value).Count(g => g.Count() > 1);
            md.AppendLine($"字典 {dictionary.Values.Count} 条{(duplicates > 0 ? $"，其中 {duplicates} 个中文名重复（选值时要附 ID 区分）" : "")}。" +
                          $"示例：{string.Join("、", dictionary.Values.Take(20).Select(v => v.Value))}");
            md.AppendLine();

            // 候选：名称相近的字段（尺码对俄罗斯尺码），或有值完全相同的字段。名称相近的排前面。
            // 只靠"包含"沾上的不算：如颜色值"黑色波点连衣裙"含"连衣裙"，并不说明颜色字段是类型的来源。
            var ranked = fields
                .Select(f => (Field: f, Related: NameLooksRelated(f.Name, attribute.Name), Match: Match(f.Values, names)))
                .Where(x => x.Related || x.Match.Exact > 0)
                .OrderByDescending(x => x.Related)
                .ThenByDescending(x => x.Match.Exact + x.Match.Contains)
                .Select(x => (x.Field, x.Match))
                .Take(CandidateCount)
                .ToList();
            if (ranked.Count == 0)
            {
                md.AppendLine("没有名称相近的 1688 字段，也没有字段的值和字典值完全相同。");
                md.AppendLine();
                continue;
            }

            md.AppendLine("| 1688 字段 | 去重值 | 完全相同 | 包含字典值 | 都不是 |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var (field, match) in ranked)
            {
                md.AppendLine($"| {field.Label} | {field.Values.Count} | {match.Exact} | {match.Contains} | {match.Unmatched.Count} |");
            }

            md.AppendLine();
            var best = ranked[0];
            if (best.Match.Unmatched.Count > 0)
            {
                md.AppendLine($"{best.Field.Label}里都不是的值：{string.Join("、", best.Match.Unmatched.Take(UnmatchedSampleCount))}");
                md.AppendLine();
            }

            if (best.Match.ContainsSamples.Count > 0)
            {
                md.AppendLine($"{best.Field.Label}里\"包含\"的例子（源值 → 包含的字典值，可能不止一个）：" +
                              string.Join("；", best.Match.ContainsSamples.Take(20)));
                md.AppendLine();
            }
        }
    }

    private static void WriteSourceFields(StringBuilder md, IReadOnlyList<SourceField> fields, int offerCount)
    {
        md.AppendLine("## 四、1688 字段全表");
        md.AppendLine();
        md.AppendLine("规格是 SKU 级（每个 SKU 一个值）；参数是商品级，按\"、\"拆成多个值。");
        md.AppendLine();
        md.AppendLine("| 字段 | 有该字段的商品 | 去重值 | 示例 |");
        md.AppendLine("|---|---|---|---|");
        foreach (var field in fields.OrderByDescending(f => f.OfferCount))
        {
            md.AppendLine($"| {field.Label} | {field.OfferCount}/{offerCount} | {field.Values.Count} | " +
                          $"{Escape(string.Join("、", field.Values.Take(8)))} |");
        }

        md.AppendLine();
    }

    private sealed record SourceField(string Kind, string Name, int OfferCount, IReadOnlyList<string> Values)
    {
        public string Label => $"{Kind}「{Name}」";
    }

    private sealed record MatchResult(int Exact, int Contains, IReadOnlyList<string> Unmatched, IReadOnlyList<string> ContainsSamples);

    /// <summary>规格轴（SKU 级）和商品参数（商品级）分开统计，同名也不合并：两者含义和用法不同。</summary>
    private static List<SourceField> CollectSourceFields(IReadOnlyList<CollectedOffer> offers)
    {
        var axes = offers
            .SelectMany(o => o.Axes.Select(a => (Offer: o.OfferId, a.Name, Values: a.Options.Select(x => x.Name))))
            .GroupBy(x => x.Name)
            .Select(g => new SourceField("规格", g.Key, g.Select(x => x.Offer).Distinct().Count(),
                g.SelectMany(x => x.Values).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList()));

        var attributes = offers
            .SelectMany(o => o.Attributes.Select(a => (Offer: o.OfferId, a.Label, a.Value)))
            .GroupBy(x => x.Label)
            .Select(g => new SourceField("参数", g.Key, g.Select(x => x.Offer).Distinct().Count(),
                g.SelectMany(x => x.Value.Split('、')).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList()));

        return [.. axes, .. attributes];
    }

    private static MatchResult Match(IReadOnlyList<string> values, HashSet<string> dictionary)
    {
        // 纯数字的字典值（尺码 44、110）不参与"包含"：源值里的体重"110斤"会误命中
        var containable = dictionary.Where(d => d.Length >= 2 && !d.All(char.IsAsciiDigit)).ToList();
        int exact = 0, contains = 0;
        var unmatched = new List<string>();
        var samples = new List<string>();
        foreach (var value in values)
        {
            if (dictionary.Contains(value))
            {
                exact++;
                continue;
            }

            var hits = containable.Where(d => value.Contains(d, StringComparison.Ordinal)).ToList();
            if (hits.Count > 0)
            {
                contains++;
                samples.Add($"{value} → {string.Join("/", hits)}");
            }
            else
            {
                unmatched.Add(value);
            }
        }

        return new MatchResult(exact, contains, unmatched, samples);
    }

    /// <summary>大字典没法比值，只按名字找候选字段，如"服装和鞋类品牌"对"品牌"。</summary>
    private static bool NameLooksRelated(string fieldName, string attributeName) =>
        attributeName.Contains(fieldName, StringComparison.Ordinal) || fieldName.Contains(attributeName, StringComparison.Ordinal);

    private static string Mark(bool value) => value ? "是" : "";

    private static string Escape(string text) => text.Replace("|", "\\|");
}
