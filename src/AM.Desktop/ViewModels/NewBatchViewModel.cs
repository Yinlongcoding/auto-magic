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

    public IReadOnlyList<OzonType> Types => SelectedCategory?.Types ?? [];

    /// <summary>成功开始采集后的新批次；取消时为 null。</summary>
    public CollectionBatch? Result { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnSelectedCategoryChanged(OzonCategory? value) => SelectedType = null;

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
        if (TypeError is not null || KeywordError is not null || CountError is not null)
        {
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
            PricingSummary = "未设置（定价尚未实现）",
            TargetCount = (int)TargetCount!.Value,
            Sort = SelectedSort,
            State = BatchState.Collecting,
        };
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
