using AM.Core.Pricing;
using AM.Desktop.Models;
using AM.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AM.Desktop.ViewModels;

/// <summary>
/// 新建采集弹窗：类目、关键词、采集参数和定价参数。开始采集后定价参数随批次冻结。
/// 校验失败时弹窗不关，在字段旁提示。
/// 定价参数没有内置默认值；选类型时从同一类型上一个批次预填（13 号）。
/// </summary>
public sealed partial class NewBatchViewModel : ObservableObject
{
    public NewBatchViewModel()
    {
        CurrencyCode = SettingsStore.LoadOzon().CurrencyCode;
    }

    public IReadOnlyList<OzonCategory> Categories => CategoryCache.Categories;
    public IReadOnlyList<string> SortOptions => BatchStore.SortOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Types))]
    private OzonCategory? _selectedCategory;

    [ObservableProperty]
    private OzonType? _selectedType;

    public bool HasCategories => Categories.Count > 0;
    public string RefreshCategoriesText => HasCategories ? "更新类目" : "加载类目";

    [ObservableProperty]
    private bool _isRefreshingCategories;

    [ObservableProperty]
    private string? _categoryMessage = CategoryUpdatedText();

    [ObservableProperty]
    private bool _categoryFailed;

    [ObservableProperty]
    private string _keyword = "";

    [ObservableProperty]
    private double? _targetCount = 50;

    [ObservableProperty]
    private string _selectedSort = BatchStore.SortOptions[0];

    [ObservableProperty]
    private string? _typeError;

    [ObservableProperty]
    private string? _keywordError;

    [ObservableProperty]
    private string? _countError;

    // 定价参数。百分比字段按界面输入的数字存（15 表示 15%），组装参数时再除以 100。

    /// <summary>售价币种，取自设置。</summary>
    public string CurrencyCode { get; }

    public string SalePriceLabel => $"售价区间 {CurrencyCode}";
    public string ExchangeRateLabel => $"汇率（1 元 = ? {CurrencyCode}）/ 浮动%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _commissionPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _logisticsCost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _targetMarginPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _minMarginPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _salePriceMin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _salePriceMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _exchangeRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _exchangeFloatPercent;

    /// <summary>采购价下限，选填，留空表示不设下限。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private double? _purchasePriceMin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PricingPreview), nameof(PricingPreviewIsError))]
    private bool _pricingAttempted;

    /// <summary>八个必填参数都填了才组装；否则为 null。采购价下限选填。</summary>
    private PricingParameters? BuildPricing() =>
        CommissionPercent is { } c && LogisticsCost is { } l && TargetMarginPercent is { } t && MinMarginPercent is { } m &&
        SalePriceMin is { } smin && SalePriceMax is { } smax && ExchangeRate is { } r && ExchangeFloatPercent is { } f
            ? new PricingParameters((decimal)c / 100, (decimal)l, (decimal)t / 100, (decimal)m / 100,
                (decimal)smin, (decimal)smax, (decimal)r, (decimal)f / 100, (decimal?)PurchasePriceMin)
            : null;

    /// <summary>参数下方的一行：反推出的采购价上限，或参数错误。</summary>
    public string PricingPreview
    {
        get
        {
            if (BuildPricing() is not { } p)
            {
                return PricingAttempted
                    ? "请填写全部定价参数。"
                    : "填完全部定价参数后，这里显示可采集的采购价上限。";
            }

            var errors = p.Validate();
            if (errors.Count > 0)
            {
                return string.Join(" ", errors);
            }

            var max = PricingEngine.MaxPurchasePrice(p);
            return p.PurchasePriceMin is { } min
                ? $"可采集的采购价 ¥{min:0.00} 到 ¥{max:0.00}（搜索时按它粗筛，详情页逐个 SKU 再判断）"
                : $"可采集的采购价上限 ¥{max:0.00}（未设下限：更便宜的商品会抬到售价下限并保留）";
        }
    }

    public bool PricingPreviewIsError =>
        BuildPricing() is { } p ? p.Validate().Count > 0 : PricingAttempted;

    public IReadOnlyList<OzonType> Types => SelectedCategory?.Types ?? [];

    /// <summary>成功开始采集后的新批次；取消时为 null。</summary>
    public CollectionBatch? Result { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnSelectedCategoryChanged(OzonCategory? value) => SelectedType = null;

    /// <summary>从同一类型最近一个批次预填定价参数，只是界面默认值。</summary>
    partial void OnSelectedTypeChanged(OzonType? value)
    {
        if (value is null || BatchStore.Batches.FirstOrDefault(b => b.TypeId == value.TypeId) is not { } last)
        {
            return;
        }

        var p = last.Pricing;
        CommissionPercent = (double)(p.CommissionRate * 100);
        LogisticsCost = (double)p.LogisticsCost;
        TargetMarginPercent = (double)(p.TargetMargin * 100);
        MinMarginPercent = (double)(p.MinMargin * 100);
        SalePriceMin = (double)p.SalePriceMin;
        SalePriceMax = (double)p.SalePriceMax;
        ExchangeRate = (double)p.ExchangeRate;
        ExchangeFloatPercent = (double)(p.ExchangeFloat * 100);
        PurchasePriceMin = (double?)p.PurchasePriceMin;
    }

    /// <summary>用已保存的 Ozon 配置重新拉类目树。失败保留旧缓存。</summary>
    [RelayCommand]
    private async Task RefreshCategoriesAsync()
    {
        var settings = SettingsStore.LoadOzon();
        if (!settings.IsComplete)
        {
            CategoryFailed = true;
            CategoryMessage = "请先在设置里填写 Ozon 的 Client-Id 和 API Key。";
            return;
        }

        IsRefreshingCategories = true;
        CategoryFailed = false;
        CategoryMessage = "正在从 Ozon 拉取类目…";
        try
        {
            await CategoryCache.RefreshAsync(settings);
            SelectedCategory = null;
            CategoryMessage = CategoryUpdatedText();
            OnPropertyChanged(nameof(Categories));
            OnPropertyChanged(nameof(HasCategories));
            OnPropertyChanged(nameof(RefreshCategoriesText));
        }
        catch (OzonApiException error)
        {
            CategoryFailed = true;
            CategoryMessage = error.Message;
        }
        finally
        {
            IsRefreshingCategories = false;
        }
    }

    private static string? CategoryUpdatedText() => CategoryCache.UpdatedAt is { } at
        ? $"类目更新于 {at:yyyy-MM-dd HH:mm}"
        : "还没有类目缓存，点“加载类目”。";

    [RelayCommand]
    private void Start()
    {
        TypeError = SelectedType is null ? "请选择类目和类型" : null;
        KeywordError = string.IsNullOrWhiteSpace(Keyword) ? "请填写关键词" : null;
        CountError = TargetCount is null or < 1 ? "采集数量至少为 1" : null;
        PricingAttempted = true;
        var pricing = BuildPricing();
        var pricingValid = pricing is not null && pricing.Validate().Count == 0;
        if (TypeError is not null || KeywordError is not null || CountError is not null || !pricingValid)
        {
            OnPropertyChanged(nameof(PricingPreview));
            return;
        }

        Result = new CollectionBatch
        {
            Id = $"b-{DateTime.Now:HHmmss}",
            Keyword = Keyword.Trim(),
            CategoryPath = $"{SelectedCategory!.Path} / {SelectedType!.Name}",
            DescriptionCategoryId = SelectedCategory.DescriptionCategoryId,
            TypeId = SelectedType.TypeId,
            CreatedAt = DateTime.Now,
            Pricing = pricing!,
            CurrencyCode = CurrencyCode,
            TargetCount = (int)TargetCount!.Value,
            Sort = SelectedSort,
            State = BatchState.Collecting,
        };
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
