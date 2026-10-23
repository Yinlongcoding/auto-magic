using AM.Core.Rules;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AM.Desktop.Models;

/// <summary>下拉框里的一个字典值。中文名重复时附 ID（颜色字典有 52 个重名，19 号）。</summary>
public sealed record DictionaryOption(long Id, string Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>待处理页"属性"分段里一个属性的分组：该属性下所有未匹配的源值。</summary>
public sealed record AttributeIssueGroup(long AttributeId, string Name, IReadOnlyList<AttributeIssueRow> Rows)
{
    public string Header => $"{Name}（{AttributeId}）：{Rows.Count} 个源值未匹配";
}

/// <summary>
/// 一个未匹配的源值（去重后）。选定的值写进批次的人工修订，本批次所有含该源值的 SKU 一起生效（19 号）。
/// 字典很小时直接给全部选项；大字典（品牌）给不了，只能写规则。
/// </summary>
public sealed partial class AttributeIssueRow : ObservableObject
{
    private readonly IReadOnlyList<DictionaryOption> _allOptions;

    public AttributeIssueRow(long attributeId, string? source, string failure, IReadOnlyList<CollectedProduct> products,
        int skuCount, IReadOnlyList<string> thumbnails, IReadOnlyList<DictionaryOption> options)
    {
        AttributeId = attributeId;
        Source = source;
        Failure = failure;
        Products = products;
        SkuCount = skuCount;
        Thumbnails = thumbnails;
        _allOptions = options;
        _filteredOptions = options;
    }

    public long AttributeId { get; }

    /// <summary>原始源值；SKU 上没有这个字段时为 null。</summary>
    public string? Source { get; }

    public string SourceText => Source ?? "（采集数据里没有这个字段）";

    /// <summary>写进人工修订的源值，缺失时是 &lt;缺失&gt;。</summary>
    public string EditSource => Source ?? RuleRow.Missing;

    public string Failure { get; }

    /// <summary>含该源值的商品，"放弃"作用于它们。</summary>
    public IReadOnlyList<CollectedProduct> Products { get; }

    public int SkuCount { get; }

    public string CountText => $"{SkuCount} 个 SKU，{Products.Count} 个商品";

    public string AbandonText => $"放弃这 {Products.Count} 个商品";

    /// <summary>最多 3 张，取 SKU 图，没有取商品主图（19 号）。</summary>
    public IReadOnlyList<string> Thumbnails { get; }

    public bool CanPick => _allOptions.Count > 0;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private IReadOnlyList<DictionaryOption> _filteredOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DictionaryOption? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>按中文名或 ID 筛，字典有几百条，直接翻找不动。</summary>
    partial void OnFilterChanged(string value)
    {
        var text = value.Trim();
        FilteredOptions = text.Length == 0
            ? _allOptions
            : _allOptions.Where(o => o.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
                                     || o.Id.ToString().StartsWith(text, StringComparison.Ordinal)).ToList();
    }
}

/// <summary>本批次已做的一条人工选择，可撤销。</summary>
public sealed record ManualEditRow(ManualEdit Edit, string AttributeName)
{
    public string Text => $"{AttributeName}：{(Edit.Source == RuleRow.Missing ? "（缺失）" : Edit.Source)} → {Edit.Label}（{Edit.ValueId}）";
}
