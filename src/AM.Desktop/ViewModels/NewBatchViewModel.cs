using AM.Desktop.Models;
using AM.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AM.Desktop.ViewModels;

/// <summary>
/// 新建采集弹窗：类目、关键词、采集参数和定价参数。开始采集后定价参数随批次冻结。
/// 校验失败时弹窗不关，在字段旁提示。
/// </summary>
public sealed partial class NewBatchViewModel : ObservableObject
{
    public IReadOnlyList<string> Categories => SampleData.Categories;
    public IReadOnlyList<string> SortOptions => SampleData.SortOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Types))]
    private string? _selectedCategory;

    [ObservableProperty]
    private string? _selectedType;

    [ObservableProperty]
    private string _keyword = "";

    [ObservableProperty]
    private double? _targetCount = 50;

    [ObservableProperty]
    private string _selectedSort = SampleData.SortOptions[0];

    [ObservableProperty]
    private string? _typeError;

    [ObservableProperty]
    private string? _keywordError;

    [ObservableProperty]
    private string? _countError;

    public IReadOnlyList<string> Types =>
        SelectedCategory is not null && SampleData.Types.TryGetValue(SelectedCategory, out var types) ? types : [];

    // 公式反推结果，真实计算在实现定价时对照 13 号文档。
    public string DerivedPurchaseRange => "采购价区间 ¥8.50 到 ¥42.00（示例）";

    /// <summary>成功开始采集后的新批次；取消时为 null。</summary>
    public CollectionBatch? Result { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnSelectedCategoryChanged(string? value) => SelectedType = null;

    [RelayCommand]
    private void Start()
    {
        TypeError = SelectedType is null ? "请选择类目和类型" : null;
        KeywordError = string.IsNullOrWhiteSpace(Keyword) ? "请填写关键词" : null;
        CountError = TargetCount is null or < 1 ? "采集数量至少为 1" : null;
        if (TypeError is not null || KeywordError is not null || CountError is not null)
        {
            return;
        }

        Result = new CollectionBatch
        {
            Id = $"b-{DateTime.Now:HHmmss}",
            Keyword = Keyword.Trim(),
            CategoryPath = $"{SelectedCategory} / {SelectedType}",
            CreatedAt = DateTime.Now,
            PricingSummary = "佣金 15% · 物流 ¥18 · 目标利润 20% · 汇率 12.1（浮动 2%）",
            TargetCount = (int)TargetCount!.Value,
            Sort = SelectedSort,
            State = BatchState.Collecting,
        };
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
