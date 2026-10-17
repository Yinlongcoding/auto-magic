using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using AutoMagic.Application.ExchangeRates;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Rules;
using AutoMagic.Application.Search;
using AutoMagic.Contracts.Protocol;
using AutoMagic.Domain.Pricing;
using AutoMagic.Infrastructure.Collection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoMagic.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISearchBridge _searchBridge;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IOzonSchemaService _ozonSchemaService;
    private readonly ILocalOzonCategoryCatalog _ozonCategoryCatalog;
    private readonly IOzonTestSettingsStore _ozonTestSettingsStore;
    private readonly CollectionSnapshotStore _collectionSnapshotStore;
    private readonly CleaningEngine _cleaningEngine;
    private readonly MatchingEngine _matchingEngine;
    private readonly Dispatcher _dispatcher;
    private bool _initialized;
    private OzonCategorySchema? _ozonSchema;
    private IReadOnlyList<DetailCollectionResultDto> _latestDetailResults = [];
    private string _latestCollectionId = string.Empty;
    private int _ozonSchemaRequestRevision;
    private CancellationTokenSource? _ozonSettingsSaveDebounce;
    private bool _isRestoringOzonSettings;
    private RuleSourceProduct? _currentSource;
    private CleanedProduct? _lastCleaned;
    private MatchedProduct? _lastMatched;

    [ObservableProperty]
    private string _keyword = "连衣裙";

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _rawJson = "尚未获取数据。";

    [ObservableProperty]
    private int _resultCount;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoadingRates;

    [ObservableProperty]
    private string _cnyToUsdText = "—";

    [ObservableProperty]
    private string _cnyToRubText = "—";

    [ObservableProperty]
    private string _exchangeRateStatus = "正在读取国际参考汇率…";

    [ObservableProperty]
    private decimal _commissionRatePercent;

    [ObservableProperty]
    private decimal _targetProfitRatePercent = 10m;

    [ObservableProperty]
    private decimal _logisticsCostCny;

    [ObservableProperty]
    private decimal _advertisingCostCny;

    [ObservableProperty]
    private decimal _otherCostCny;

    [ObservableProperty]
    private decimal? _salePriceMinimumCny;

    [ObservableProperty]
    private decimal? _salePriceMaximumCny;

    [ObservableProperty]
    private string _selectedSortMode = ProductSortModes.Sales;

    [ObservableProperty]
    private string _commissionCostText = "—";

    [ObservableProperty]
    private string _targetProfitCostText = "—";

    [ObservableProperty]
    private string _procurementCostText = "—";

    [ObservableProperty]
    private string _ozonClientId = string.Empty;

    [ObservableProperty]
    private string _ozonApiKey = string.Empty;

    [ObservableProperty]
    private OzonCategoryOption? _selectedOzonCategory;

    [ObservableProperty]
    private OzonTypeOption? _selectedOzonType;

    [ObservableProperty]
    private bool _isLoadingOzonCatalog;

    [ObservableProperty]
    private string _ozonCatalogStatus = "正在读取本地 Ozon 测试品类目录…";

    [ObservableProperty]
    private bool _isLoadingOzonSchema;

    [ObservableProperty]
    private string _ozonSchemaStatus = "测试设置会自动恢复；API Key 保存在 Windows Credential Manager。";

    [ObservableProperty]
    private string _ozonSchemaJson = "尚未读取 Ozon Schema。";

    [ObservableProperty]
    private DetailCollectionResultDto? _selectedCleaningDetail;

    [ObservableProperty]
    private string _cleaningSourceStatus = "完成详情采集后，选择商品并执行数据清洗。";

    [ObservableProperty]
    private string _cleanedProductJson = "选择一个已采集的商品以查看数据清洗结果。";

    [ObservableProperty]
    private int _selectedCleaningTabIndex = 0;

    public MainViewModel(
        ISearchBridge searchBridge,
        IExchangeRateService exchangeRateService,
        IOzonSchemaService ozonSchemaService,
        ILocalOzonCategoryCatalog ozonCategoryCatalog,
        IOzonTestSettingsStore ozonTestSettingsStore,
        CollectionSnapshotStore collectionSnapshotStore,
        CleaningEngine cleaningEngine,
        MatchingEngine matchingEngine)
    {
        _searchBridge = searchBridge;
        _exchangeRateService = exchangeRateService;
        _ozonSchemaService = ozonSchemaService;
        _ozonCategoryCatalog = ozonCategoryCatalog;
        _ozonTestSettingsStore = ozonTestSettingsStore;
        _collectionSnapshotStore = collectionSnapshotStore;
        _cleaningEngine = cleaningEngine;
        _matchingEngine = matchingEngine;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _statusText = GetConnectionText(searchBridge.IsExtensionConnected);
        _searchBridge.ConnectionChanged += OnConnectionChanged;
        _searchBridge.SearchProgressChanged += OnSearchProgressChanged;
    }

    public ObservableCollection<ProductItemDto> Products { get; } = [];

    public ObservableCollection<OzonAttributeDefinition> OzonAttributes { get; } = [];

    public ObservableCollection<OzonCategoryOption> OzonCategoryOptions { get; } = [];

    public ObservableCollection<OzonTypeOption> OzonTypeOptions { get; } = [];

    public ObservableCollection<DetailCollectionResultDto> CleaningProducts { get; } = [];

    public ObservableCollection<SourceFact> SourceFacts { get; } = [];

    public IReadOnlyList<ProductSortOption> SortOptions { get; } =
    [
        new(ProductSortModes.Sales, "销量优先"),
        new(ProductSortModes.PriceAscending, "价格优先"),
    ];

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await Task.WhenAll(RefreshExchangeRatesAsync(), LoadOzonCatalogAsync());
        await RestoreOzonTestSettingsAsync();
        await RestoreLatestCollectionAsync();
    }

    private bool CanSearch() =>
        !IsBusy &&
        _searchBridge.IsExtensionConnected &&
        !string.IsNullOrWhiteSpace(Keyword);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (!TryGetCurrentCostRange(out var costRange, out var validationMessage))
        {
            StatusText = validationMessage;
            return;
        }

        IsBusy = true;
        StatusText = $"Chrome 正在搜索“{Keyword.Trim()}”，筛选采购成本 {costRange.ProcurementMinimumCny:0.00} - {costRange.ProcurementMaximumCny:0.00} CNY…";
        Products.Clear();
        ResultCount = 0;
        RawJson = "等待插件返回数据…";
        _latestDetailResults = [];
        _latestCollectionId = string.Empty;
        CleaningProducts.Clear();
        SelectedCleaningDetail = null;
        ClearCleaningPreview("正在等待新的详情采集结果。");

        try
        {
            var result = await _searchBridge.SearchAsync(
                Keyword,
                60,
                costRange.ProcurementMinimumCny,
                costRange.ProcurementMaximumCny,
                SelectedSortMode,
                includeDetailFacts: true,
                cancellationToken: CancellationToken.None);
            var snapshotPath = await _collectionSnapshotStore.SaveAsync(result);
            ApplyCollectionResult(result, Path.GetFileName(snapshotPath));
            var successCount = _latestDetailResults.Count(item => item.Status == "success");
            var partialCount = _latestDetailResults.Count(item => item.Status == "partial");
            var failedCount = _latestDetailResults.Count(item => item.Status == "failed");
            StatusText =
                $"采集完成：列表 {result.Count} 条；详情已处理 {_latestDetailResults.Count} 条（成功 {successCount}、部分 {partialCount}、失败 {failedCount}）；已保存至 {snapshotPath}。";
        }
        catch (Exception error)
        {
            RawJson = "";
            StatusText = $"采集失败：{error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreLatestCollectionAsync()
    {
        try
        {
            var stored = await _collectionSnapshotStore.LoadLatestAsync();
            if (stored is null)
            {
                return;
            }

            ApplyCollectionResult(stored.Result, Path.GetFileName(stored.Directory));
            StatusText =
                $"已加载最近采集批次 {_latestCollectionId}：列表 {stored.Result.Count} 条，详情 {_latestDetailResults.Count} 条。";
        }
        catch (Exception error)
        {
            CleaningSourceStatus = $"恢复最近采集结果失败：{error.Message}";
        }
    }

    private void ApplyCollectionResult(SearchResultPayload result, string collectionId)
    {
        Products.Clear();
        foreach (var item in result.Items) Products.Add(item);
        ResultCount = result.Count;
        RawJson = JsonSerializer.Serialize(result, BridgeJson.IndentedOptions);
        _latestCollectionId = collectionId;
        _latestDetailResults = result.DetailResults ?? [];
        var mappableDetails = _latestDetailResults.Where(IsMappableDetail).ToArray();
        CleaningProducts.Clear();
        foreach (var detail in mappableDetails) CleaningProducts.Add(detail);
        SelectedCleaningDetail = mappableDetails.FirstOrDefault(item =>
            string.Equals(item.Status, "success", StringComparison.OrdinalIgnoreCase))
            ?? mappableDetails.FirstOrDefault();
        RefreshCleaningInput();
    }

    private static bool IsMappableDetail(DetailCollectionResultDto detail) =>
        detail.Status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
        detail.Status.Equals("partial", StringComparison.OrdinalIgnoreCase);

    private bool CanLoadOzonSchema() =>
        !IsLoadingOzonSchema &&
        !string.IsNullOrWhiteSpace(OzonClientId) &&
        !string.IsNullOrWhiteSpace(OzonApiKey) &&
        SelectedOzonCategory is not null &&
        SelectedOzonType is not null;

    [RelayCommand(CanExecute = nameof(CanLoadOzonSchema))]
    private async Task LoadOzonSchemaAsync()
    {
        if (SelectedOzonCategory is null)
        {
            OzonSchemaStatus = "请先选择 Ozon 品类。";
            return;
        }

        if (SelectedOzonType is null)
        {
            OzonSchemaStatus = "请先选择 Ozon 商品类型。";
            return;
        }

        var selectedCategory = SelectedOzonCategory;
        var selectedType = SelectedOzonType;
        var categoryId = selectedCategory.DescriptionCategoryId;
        var typeId = selectedType.TypeId;
        var requestRevision = Interlocked.Increment(ref _ozonSchemaRequestRevision);

        IsLoadingOzonSchema = true;
        OzonSchemaStatus = "正在使用临时凭证读取 Ozon 动态属性 Schema…";
        OzonAttributes.Clear();
        try
        {
            await SaveOzonTestSettingsAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var schema = await _ozonSchemaService.GetCategorySchemaAsync(
                new OzonTemporaryCredentials(OzonClientId, OzonApiKey),
                categoryId,
                typeId,
                timeout.Token);
            if (requestRevision != Volatile.Read(ref _ozonSchemaRequestRevision))
            {
                OzonSchemaStatus = "读取期间品类、类型或凭证发生变化，本次 Schema 结果已丢弃。";
                return;
            }

            _ozonSchema = schema;
            CleanProductCommand.NotifyCanExecuteChanged();
            foreach (var attribute in _ozonSchema.Attributes)
            {
                OzonAttributes.Add(attribute);
            }

            OzonSchemaJson = JsonSerializer.Serialize(_ozonSchema, BridgeJson.IndentedOptions);
            OzonSchemaStatus =
                $"凭证验证成功：{selectedCategory.DisplayName} > {selectedType.Name}；读取 {_ozonSchema.Attributes.Count} 个属性，其中必填 {_ozonSchema.RequiredCount} 个、变体 {_ozonSchema.AspectCount} 个。";
            try
            {
                _schemaCache.Save(schema);
                OzonSchemaStatus += " 已保存开发缓存，下次自动读取。";
            }
            catch (Exception cacheError) when (cacheError is IOException or UnauthorizedAccessException)
            {
                OzonSchemaStatus += $" 本次 Schema 可用，但缓存保存失败：{cacheError.Message}";
            }
        }
        catch (Exception error)
        {
            if (requestRevision != Volatile.Read(ref _ozonSchemaRequestRevision))
            {
                return;
            }

            _ozonSchema = null;
            CleanProductCommand.NotifyCanExecuteChanged();
            OzonSchemaJson = string.Empty;
            OzonSchemaStatus = $"Ozon Schema 读取失败：{error.Message}";
        }
        finally
        {
            IsLoadingOzonSchema = false;
        }
    }

    private async Task LoadOzonCatalogAsync()
    {
        IsLoadingOzonCatalog = true;
        OzonCatalogStatus = "正在读取本地 Ozon 测试品类目录…";
        try
        {
            var catalog = await _ozonCategoryCatalog.LoadAsync(CancellationToken.None);
            OzonCategoryOptions.Clear();
            foreach (var category in catalog.Categories)
            {
                OzonCategoryOptions.Add(category);
            }

            OzonCatalogStatus =
                $"本地测试目录：{catalog.Categories.Count} 个可选品类，{catalog.TypeCount} 个商品类型；来源 {catalog.Source}。";
        }
        catch (Exception error)
        {
            OzonCategoryOptions.Clear();
            OzonTypeOptions.Clear();
            OzonCatalogStatus = $"本地 Ozon 测试品类目录读取失败：{error.Message}";
        }
        finally
        {
            IsLoadingOzonCatalog = false;
        }
    }

    private async Task RestoreOzonTestSettingsAsync()
    {
        try
        {
            var settings = await _ozonTestSettingsStore.LoadAsync(CancellationToken.None);
            _isRestoringOzonSettings = true;
            try
            {
                OzonClientId = settings.ClientId;
                OzonApiKey = settings.ApiKey;
                SelectedOzonCategory = settings.DescriptionCategoryId is { } categoryId
                    ? OzonCategoryOptions.FirstOrDefault(
                        category => category.DescriptionCategoryId == categoryId)
                    : null;
                SelectedOzonType = settings.TypeId is { } typeId
                    ? OzonTypeOptions.FirstOrDefault(type => type.TypeId == typeId)
                    : null;
            }
            finally
            {
                _isRestoringOzonSettings = false;
            }

            var restoredFields = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.ClientId)) restoredFields.Add("Client ID");
            if (!string.IsNullOrWhiteSpace(settings.ApiKey)) restoredFields.Add("API Key");
            if (SelectedOzonCategory is not null) restoredFields.Add("品类");
            if (SelectedOzonType is not null) restoredFields.Add("商品类型");
            OzonSchemaStatus = restoredFields.Count == 0
                ? "尚无已保存的 Ozon 测试设置。"
                : $"已恢复：{string.Join("、", restoredFields)}。可以验证凭证并读取 Schema。";
            RestoreCachedSchema();
        }
        catch (Exception error)
        {
            OzonSchemaStatus = $"恢复 Ozon 测试设置失败：{error.Message}";
        }
    }

    private void ScheduleOzonSettingsSave()
    {
        if (_isRestoringOzonSettings) return;
        CancelPendingOzonSettingsSave();
        var cancellation = new CancellationTokenSource();
        _ozonSettingsSaveDebounce = cancellation;
        _ = SaveOzonTestSettingsAfterDelayAsync(cancellation);
    }

    private async Task SaveOzonTestSettingsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(600, cancellation.Token);
            await SaveOzonTestSettingsAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 用户仍在输入，等待下一次防抖保存。
        }
        catch (Exception error)
        {
            OzonSchemaStatus = $"保存 Ozon 测试设置失败：{error.Message}";
        }
        finally
        {
            if (ReferenceEquals(_ozonSettingsSaveDebounce, cancellation))
            {
                _ozonSettingsSaveDebounce = null;
            }
            cancellation.Dispose();
        }
    }

    private Task SaveOzonTestSettingsAsync(CancellationToken cancellationToken) =>
        _ozonTestSettingsStore.SaveAsync(
            new OzonTestSettings(
                OzonClientId,
                OzonApiKey,
                SelectedOzonCategory?.DescriptionCategoryId,
                SelectedOzonType?.TypeId),
            cancellationToken);

    private void CancelPendingOzonSettingsSave()
    {
        var pending = Interlocked.Exchange(ref _ozonSettingsSaveDebounce, null);
        pending?.Cancel();
    }

    public async Task ClearTemporaryOzonCredentialsAsync()
    {
        CancelPendingOzonSettingsSave();
        _isRestoringOzonSettings = true;
        Interlocked.Increment(ref _ozonSchemaRequestRevision);
        try
        {
            OzonClientId = string.Empty;
            OzonApiKey = string.Empty;
        }
        finally
        {
            _isRestoringOzonSettings = false;
        }
        _ozonSchema = null;
        CleanProductCommand.NotifyCanExecuteChanged();
        OzonAttributes.Clear();
        OzonSchemaJson = "尚未读取 Ozon Schema。";
        try
        {
            await _ozonTestSettingsStore.ClearCredentialsAsync(CancellationToken.None);
            OzonSchemaStatus = "已从 Windows Credential Manager 删除 API Key，并清除已保存的 Client ID。";
        }
        catch (Exception error)
        {
            OzonSchemaStatus = $"界面凭证已清空，但删除已保存凭证失败：{error.Message}";
        }
    }

    private void RefreshCleaningInput()
    {
        InvalidateCleaningResult();
        SourceFacts.Clear();
        if (SelectedCleaningDetail is null)
        {
            ClearCleaningPreview("当前没有可供数据清洗的详情结果。");
            return;
        }

        var source = RuleSourceBuilder.Create(SelectedCleaningDetail);
        _currentSource = source;
        CleanProductCommand.NotifyCanExecuteChanged();
        foreach (var (label, value) in source.Facts) SourceFacts.Add(new(label, value));
        foreach (var axis in source.Skus.SelectMany(s => s.SpecValues.Keys).Distinct())
        {
            if (source.Facts.ContainsKey(axis)) continue;
            SourceFacts.Add(new(axis, string.Join("、", source.Skus.Select(s => s.SpecValues.GetValueOrDefault(axis)).OfType<string>().Distinct())));
        }

        CleaningSourceStatus = $"商品 #{SelectedCleaningDetail.ItemPosition} · offerId {source.OfferId} · 源属性 {SourceFacts.Count} 项 · SKU {source.Skus.Count} 个 · 清洗目标：{SelectedOzonCategory?.DescriptionCategoryId.ToString() ?? "未选择类目"} / {SelectedOzonType?.TypeId.ToString() ?? "未选择类型"}";
    }

    private void ClearCleaningPreview(string status)
    {
        SourceFacts.Clear();
        _currentSource = null;
        InvalidateCleaningResult();
        CleaningSourceStatus = status;
    }

    private static int GetUnresolvedDetailUrlCount(JsonElement? diagnostics)
    {
        if (diagnostics is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("detailUrlResolution", out var resolution) ||
            resolution.ValueKind != JsonValueKind.Object ||
            !resolution.TryGetProperty("remainingCount", out var remaining) ||
            !remaining.TryGetInt32(out var count))
        {
            return 0;
        }

        return Math.Max(0, count);
    }

    [RelayCommand]
    private async Task RefreshExchangeRatesAsync()
    {
        IsLoadingRates = true;
        ExchangeRateStatus = "正在读取国际参考汇率…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var rates = await _exchangeRateService.GetLatestAsync(timeout.Token);
            CnyToUsdText = rates.CnyToUsd.ToString("0.000000");
            CnyToRubText = rates.CnyToRub.ToString("0.0000");
            ExchangeRateStatus = $"{rates.PublishedAt:yyyy-MM-dd HH:mm:ss} · {rates.Source}";
        }
        catch (Exception error)
        {
            CnyToUsdText = "—";
            CnyToRubText = "—";
            ExchangeRateStatus = $"汇率获取失败：{error.Message}";
        }
        finally
        {
            IsLoadingRates = false;
        }
    }

    partial void OnKeywordChanged(string value) => SearchCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        SearchCommand.NotifyCanExecuteChanged();
        CleanProductCommand.NotifyCanExecuteChanged();
        ExportCleanedProductCommand.NotifyCanExecuteChanged();
    }

    partial void OnOzonClientIdChanged(string value)
    {
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnOzonApiKeyChanged(string value)
    {
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnSelectedOzonCategoryChanged(OzonCategoryOption? value)
    {
        OzonTypeOptions.Clear();
        if (value is not null)
        {
            foreach (var type in value.Types)
            {
                OzonTypeOptions.Add(type);
            }
        }

        SelectedOzonType = null;
        ResetOzonSchemaForSelection();
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnSelectedOzonTypeChanged(OzonTypeOption? value)
    {
        ResetOzonSchemaForSelection();
        RestoreCachedSchema();
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnSelectedCleaningDetailChanged(DetailCollectionResultDto? value)
    {
        RefreshCleaningInput();
    }

    partial void OnIsLoadingOzonSchemaChanged(bool value)
    {
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
    }

    private void ResetOzonSchemaForSelection()
    {
        Interlocked.Increment(ref _ozonSchemaRequestRevision);
        _ozonSchema = null;
        OzonAttributes.Clear();
        OzonSchemaJson = "尚未读取 Ozon Schema。";
        OzonSchemaStatus = SelectedOzonCategory is null
            ? "请从本地测试目录选择 Ozon 品类和商品类型。"
            : SelectedOzonType is null
                ? $"已选择品类：{SelectedOzonCategory.DisplayName}；请选择商品类型。"
                : $"已选择：{SelectedOzonCategory.DisplayName} > {SelectedOzonType.Name}；可以验证凭证并读取 Schema。";
        RefreshCleaningInput();
    }

    partial void OnCommissionRatePercentChanged(decimal value) => RefreshCostAnalysis();

    partial void OnTargetProfitRatePercentChanged(decimal value) => RefreshCostAnalysis();

    partial void OnLogisticsCostCnyChanged(decimal value) => RefreshCostAnalysis();

    partial void OnAdvertisingCostCnyChanged(decimal value) => RefreshCostAnalysis();

    partial void OnOtherCostCnyChanged(decimal value) => RefreshCostAnalysis();

    partial void OnSalePriceMinimumCnyChanged(decimal? value) => RefreshCostAnalysis();

    partial void OnSalePriceMaximumCnyChanged(decimal? value) => RefreshCostAnalysis();

    private void RefreshCostAnalysis()
    {
        if (!TryValidateInputs(out var saleMinimum, out var saleMaximum))
        {
            CommissionCostText = "—";
            TargetProfitCostText = "—";
            ProcurementCostText = "—";
            return;
        }

        var pricingInput = CreatePricingInput(saleMinimum, saleMaximum);
        var costRange = ProfitCalculator.CalculateCostRange(pricingInput);
        CommissionCostText = FormatRange(
            costRange.CommissionMinimumCny,
            costRange.CommissionMaximumCny,
            "CNY");
        TargetProfitCostText = FormatRange(
            costRange.TargetProfitMinimumCny,
            costRange.TargetProfitMaximumCny,
            "CNY");
        ProcurementCostText = FormatRange(
            costRange.ProcurementMinimumCny,
            costRange.ProcurementMaximumCny,
            "CNY");
    }

    private bool TryGetCurrentCostRange(
        out CostRange costRange,
        out string validationMessage)
    {
        costRange = default!;
        validationMessage = "请先填写有效的售价区间和成本参数。";
        if (!TryValidateInputs(out var saleMinimum, out var saleMaximum))
        {
            return false;
        }

        costRange = ProfitCalculator.CalculateCostRange(
            CreatePricingInput(saleMinimum, saleMaximum));
        if (costRange.ProcurementMinimumCny < 0 ||
            costRange.ProcurementMaximumCny < 0 ||
            costRange.ProcurementMinimumCny > costRange.ProcurementMaximumCny)
        {
            validationMessage = "当前成本和目标利润超出售价区间，无法得到有效的采购成本区间。";
            return false;
        }

        return true;
    }

    private bool TryValidateInputs(
        out decimal saleMinimum,
        out decimal saleMaximum)
    {
        saleMinimum = SalePriceMinimumCny ?? 0;
        saleMaximum = SalePriceMaximumCny ?? 0;

        if (CommissionRatePercent is < 0 or > 100)
        {
            return false;
        }

        if (TargetProfitRatePercent is < 0 or > 100)
        {
            return false;
        }

        if (LogisticsCostCny < 0 ||
            AdvertisingCostCny < 0 ||
            OtherCostCny < 0)
        {
            return false;
        }

        if (SalePriceMinimumCny is null || SalePriceMaximumCny is null)
        {
            return false;
        }

        if (saleMinimum < 0 || saleMaximum < 0)
        {
            return false;
        }

        if (saleMinimum > saleMaximum)
        {
            return false;
        }

        return true;
    }

    private PricingCalculationInput CreatePricingInput(
        decimal saleMinimum,
        decimal saleMaximum) =>
        new(
            CommissionRatePercent,
            TargetProfitRatePercent,
            LogisticsCostCny,
            AdvertisingCostCny,
            OtherCostCny,
            saleMinimum,
            saleMaximum);

    private void OnConnectionChanged(object? sender, bool connected)
    {
        void Update()
        {
            if (!IsBusy)
            {
                StatusText = GetConnectionText(connected);
            }

            SearchCommand.NotifyCanExecuteChanged();
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    private void OnSearchProgressChanged(object? sender, SearchProgressPayload progress)
    {
        void Update()
        {
            if (!IsBusy) return;
            var stage = progress.Stage switch
            {
                "search_page" => "正在准备 1688 搜索页",
                "search_page_ready" => "搜索页已就绪，正在应用筛选",
                "list_loading" => "正在滚动加载商品列表",
                "list_ready" => "商品列表已就绪",
                "first_pass" => "正在串行采集详情",
                "retry" => "正在二次采集失败详情",
                _ => progress.Stage,
            };
            StatusText = $"{stage}：{progress.CompletedItems}/{progress.TotalItems}" +
                (string.IsNullOrWhiteSpace(progress.Message) ? string.Empty : $"；{progress.Message}");
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        if (_dispatcher.CheckAccess()) Update();
        else _dispatcher.BeginInvoke(Update);
    }

    private static string GetConnectionText(bool connected) => connected
        ? "Chrome 插件已连接，可以开始搜索。"
        : "等待 Chrome 插件连接。请先确认原生消息宿主已注册、插件已启用。";

    private static string FormatRange(decimal minimum, decimal maximum, string unit) =>
        $"{minimum:0.00} - {maximum:0.00} {unit}";
}

public sealed record ProductSortOption(string Value, string Label);
