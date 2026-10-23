using AM.Core.Pricing;
using AM.Core.Rules;
using AM.Desktop.Models;

namespace AM.Desktop.Services;

/// <summary>批次匹配的上下文：用的哪份属性和字典、哪些属性没匹配上就阻断。Problem 不为 null 时匹配没做成。</summary>
public sealed record BatchMatchingState(string? Problem, TypeSchema? Schema, RuleSet? Rules, IReadOnlySet<long> BlockingAttributes);

/// <summary>
/// 给批次里每个要提交的 SKU 跑规则匹配（18 号），结果写回商品，并重算批次的阻断数。
/// 只读本地缓存和规则文件，不联网（08 号问题 6）：属性没拉到就整批阻断，等用户拉取后重跑。
/// 阻断：规则声明了、且 Ozon 标为必填或变体的属性没匹配上（03 号阻断点 1、2）。
/// </summary>
public static class BatchMatching
{
    private static readonly Dictionary<string, BatchMatchingState> States = [];

    /// <summary>最近一次匹配的上下文，没跑过为 null。</summary>
    public static BatchMatchingState? StateFor(CollectionBatch batch) => States.GetValueOrDefault(batch.Id);

    /// <summary>按当前规则、字典和本批次的人工选择重算。返回有属性未匹配的商品数。</summary>
    public static int Apply(CollectionBatch batch)
    {
        var products = BatchStore.ProductsFor(batch);
        var schema = TypeSchemaCache.Load(batch.DescriptionCategoryId, batch.TypeId);
        RuleSet? rules = null;
        string? problem = null;
        if (schema is null)
        {
            problem = "还没有拉到这个类型的 Ozon 属性和字典，无法匹配属性。";
        }
        else
        {
            try
            {
                rules = RuleStore.Load();
            }
            catch (RuleFileException error)
            {
                problem = $"规则加载失败，无法匹配属性：{error.Message}";
            }
        }

        if (schema is null || rules is null)
        {
            foreach (var product in products)
            {
                product.AttributeMatches = null;
            }

            States[batch.Id] = new(problem, schema, rules, new HashSet<long>());
            return RecountBlocking(batch);
        }

        var blocking = rules.Attributes
            .Select(a => a.AttributeId)
            .Where(id => schema.Attributes.Any(a => a.Id == id && (a.IsRequired || a.IsAspect)))
            .ToHashSet();
        var dictionaries = RuleStore.DictionariesFor(schema);
        var edits = batch.ManualEdits.ToList();

        foreach (var product in products)
        {
            if (product.Offer is not { } offer)
            {
                continue;
            }

            product.AttributeMatches = offer.Skus
                .Select((sku, i) => IsSubmitted(product, i)
                    ? RuleEngine.Resolve(rules, batch.DescriptionCategoryId, batch.TypeId,
                        RuleStore.SourceFor(offer, sku), dictionaries, edits)
                    : null)
                .ToList();
        }

        States[batch.Id] = new(null, schema, rules, blocking);
        return RecountBlocking(batch);
    }

    /// <summary>放弃商品或重新匹配后重算阻断数：未放弃且价格异常或属性未匹配的商品数（阻断按商品计，放弃是商品级的）。返回其中属性未匹配的商品数。</summary>
    public static int RecountBlocking(CollectionBatch batch)
    {
        var state = StateFor(batch);
        var active = BatchStore.ProductsFor(batch).Where(p => !p.IsAbandoned && p.Offer is not null).ToList();
        var attributeIssues = active.Count(p => HasAttributeIssue(p, state));
        batch.BlockingCount = active.Count(p => HasPriceIssue(p) || HasAttributeIssue(p, state));
        return attributeIssues;
    }

    public static bool HasPriceIssue(CollectedProduct product) =>
        product.Prices?.Any(x => x.State == SkuPriceState.Abnormal) == true;

    /// <summary>该 SKU 的阻断属性里没匹配上的。</summary>
    public static IEnumerable<AttributeMatch> UnmatchedBlocking(IReadOnlyList<AttributeMatch>? sku, BatchMatchingState state) =>
        sku?.Where(m => !m.IsMatched && state.BlockingAttributes.Contains(m.AttributeId)) ?? [];

    private static bool HasAttributeIssue(CollectedProduct product, BatchMatchingState? state)
    {
        if (state is null)
        {
            return false;
        }

        // 没法匹配时，有要提交的 SKU 的商品都算阻断
        if (state.Problem is not null)
        {
            return product.Offer!.Skus.Select((_, i) => i).Any(i => IsSubmitted(product, i));
        }

        return product.AttributeMatches?.Any(sku => UnmatchedBlocking(sku, state).Any()) == true;
    }

    /// <summary>价格丢弃的 SKU 不提交，不用匹配；还没定价时都算要提交。</summary>
    private static bool IsSubmitted(CollectedProduct product, int skuIndex) =>
        product.Prices is not { } prices || prices[skuIndex].State != SkuPriceState.Dropped;
}
