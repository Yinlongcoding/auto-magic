using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using AutoMagic.Application.ExchangeRates;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
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
    private readonly IQwenTestSettingsStore _qwenTestSettingsStore;
    private readonly CollectionSnapshotStore _collectionSnapshotStore;
    private readonly ProductMappingRunner _productMappingRunner;
    private readonly CategoryRuleMatchingEngine _categoryRuleEngine;
    private readonly Dispatcher _dispatcher;
    private bool _initialized;
    private OzonCategorySchema? _ozonSchema;
    private DetailFactSnapshotDto? _latestDetailSnapshot;
    private IReadOnlyList<DetailCollectionResultDto> _latestDetailResults = [];
    private string _latestCollectionId = string.Empty;
    private int _ozonSchemaRequestRevision;
    private CancellationTokenSource? _ozonSettingsSaveDebounce;
    private bool _isRestoringOzonSettings;
    private CancellationTokenSource? _qwenSettingsSaveDebounce;
    private bool _isRestoringQwenSettings;
    private FieldMatchingInput? _latestFieldMatchingInput;
    private CancellationTokenSource? _ruleMappingCancellation;

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
    private string _qwenApiKey = string.Empty;

    [ObservableProperty]
    private bool _isRunningQwenMapping;

    [ObservableProperty]
    private string _qwenMappingStatus = "AI 调用已暂停；准备 Ozon Schema 和 1688 详情后可预览输入 JSON。";

    [ObservableProperty]
    private string _qwenRequestJson = "尚未生成Qwen映射请求。";

    [ObservableProperty]
    private string _qwenRawResponseJson = "AI 调用已暂停。";

    [ObservableProperty]
    private string _qwenDictionaryStatus = "AI 调用已暂停；当前不查询字典。";

    [ObservableProperty]
    private DetailCollectionResultDto? _selectedFieldMatchingDetail;

    [ObservableProperty]
    private string _fieldMatchingStatus = "完成一次详情采集后，可以在这里审阅匹配输入。";

    [ObservableProperty]
    private string _fieldMatchingInputJson = "尚未生成字段匹配输入。";

    [ObservableProperty]
    private string _cleanedProductJson = "选择一个已采集的商品以查看数据清洗结果。";

    [ObservableProperty]
    private int _selectedFieldMatchingTabIndex = 4;

    [ObservableProperty]
    private string _fieldMatchingFinalOutputJson = "尚未生成统一最终输出。";

    [ObservableProperty]
    private string _fieldMatchingFinalStatus = "等待字段匹配输入和引擎计划。";

    [ObservableProperty]
    private string _productSkuPlanStatus = "等待 SKU 采集结果。";

    public MainViewModel(
        ISearchBridge searchBridge,
        IExchangeRateService exchangeRateService,
        IOzonSchemaService ozonSchemaService,
        ILocalOzonCategoryCatalog ozonCategoryCatalog,
        IOzonTestSettingsStore ozonTestSettingsStore,
        IQwenTestSettingsStore qwenTestSettingsStore,
        CollectionSnapshotStore collectionSnapshotStore,
        ProductMappingRunner productMappingRunner,
        CategoryRuleMatchingEngine categoryRuleEngine)
    {
        _searchBridge = searchBridge;
        _exchangeRateService = exchangeRateService;
        _ozonSchemaService = ozonSchemaService;
        _ozonCategoryCatalog = ozonCategoryCatalog;
        _ozonTestSettingsStore = ozonTestSettingsStore;
        _qwenTestSettingsStore = qwenTestSettingsStore;
        _collectionSnapshotStore = collectionSnapshotStore;
        _productMappingRunner = productMappingRunner;
        _categoryRuleEngine = categoryRuleEngine;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _statusText = GetConnectionText(searchBridge.IsExtensionConnected);
        _searchBridge.ConnectionChanged += OnConnectionChanged;
        _searchBridge.SearchProgressChanged += OnSearchProgressChanged;
    }

    public ObservableCollection<ProductItemDto> Products { get; } = [];

    public ObservableCollection<OzonAttributeDefinition> OzonAttributes { get; } = [];

    public ObservableCollection<OzonCategoryOption> OzonCategoryOptions { get; } = [];

    public ObservableCollection<OzonTypeOption> OzonTypeOptions { get; } = [];

    public ObservableCollection<DetailCollectionResultDto> FieldMatchingProducts { get; } = [];

    public ObservableCollection<FieldMatchingSourceFact> FieldMatchingFacts { get; } = [];

    public ObservableCollection<ProductMappingRow> ProductMappingRows { get; } = [];
    public ObservableCollection<ProductMappingIssue> ProductMappingIssues { get; } = [];
    public ObservableCollection<ProductSkuIdentityPlanItem> ProductSkuIdentityRows { get; } = [];

    public string QwenModelId => QwenMappingRuntime.ModelId;

    public string QwenRuntimeDescription =>
        $"{QwenMappingRuntime.Provider} · {QwenMappingRuntime.RegionDisplayName} · 非思考模式 · JSON Schema";

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
        await Task.WhenAll(RestoreOzonTestSettingsAsync(), RestoreQwenTestSettingsAsync());
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
        _latestDetailSnapshot = null;
        _latestDetailResults = [];
        _latestCollectionId = string.Empty;
        FieldMatchingProducts.Clear();
        SelectedFieldMatchingDetail = null;
        ClearFieldMatchingPreview("正在等待新的详情采集结果。");
        ResetQwenMappingOutput("正在等待新的1688详情事实。");

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
            FieldMatchingStatus = $"恢复最近采集结果失败：{error.Message}";
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
        FieldMatchingProducts.Clear();
        foreach (var detail in mappableDetails) FieldMatchingProducts.Add(detail);
        SelectedFieldMatchingDetail = mappableDetails.FirstOrDefault(item =>
            string.Equals(item.Status, "success", StringComparison.OrdinalIgnoreCase))
            ?? mappableDetails.FirstOrDefault();
        _latestDetailSnapshot = CreateDetailSnapshot(SelectedFieldMatchingDetail, result.CapturedAt)
            ?? (_latestDetailResults.Count == 0 ? result.DetailSnapshot : null);
        RefreshFieldMatchingInput();
        RefreshQwenReadinessStatus();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
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
            foreach (var attribute in _ozonSchema.Attributes)
            {
                OzonAttributes.Add(attribute);
            }

            OzonSchemaJson = JsonSerializer.Serialize(_ozonSchema, BridgeJson.IndentedOptions);
            OzonSchemaStatus =
                $"凭证验证成功：{selectedCategory.DisplayName} > {selectedType.Name}；读取 {_ozonSchema.Attributes.Count} 个属性，其中必填 {_ozonSchema.RequiredCount} 个。";
            RefreshFieldMatchingInput();
            ResetQwenMappingOutput("Schema 和详情事实就绪后，可以生成 AI 输入 JSON。");
            RefreshQwenReadinessStatus();
        }
        catch (Exception error)
        {
            if (requestRevision != Volatile.Read(ref _ozonSchemaRequestRevision))
            {
                return;
            }

            _ozonSchema = null;
            OzonSchemaJson = string.Empty;
            OzonSchemaStatus = $"Ozon Schema 读取失败：{error.Message}";
            ResetQwenMappingOutput("Ozon Schema 不可用，无法生成 AI 输入 JSON。");
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
        }
        catch (Exception error)
        {
            OzonSchemaStatus = $"恢复 Ozon 测试设置失败：{error.Message}";
        }
    }

    private async Task RestoreQwenTestSettingsAsync()
    {
        try
        {
            var settings = await _qwenTestSettingsStore.LoadAsync(CancellationToken.None);
            _isRestoringQwenSettings = true;
            try
            {
                QwenApiKey = settings.ApiKey;
            }
            finally
            {
                _isRestoringQwenSettings = false;
            }

            QwenMappingStatus = string.IsNullOrWhiteSpace(settings.ApiKey)
                ? "AI 调用已暂停；预览输入 JSON 无需百炼 API Key。"
                : "已从Windows Credential Manager恢复百炼API Key。";
            RefreshQwenReadinessStatus();
        }
        catch (Exception error)
        {
            QwenMappingStatus = $"恢复百炼测试凭证失败：{error.Message}";
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

    private void ScheduleQwenSettingsSave()
    {
        if (_isRestoringQwenSettings) return;
        CancelPendingQwenSettingsSave();
        var cancellation = new CancellationTokenSource();
        _qwenSettingsSaveDebounce = cancellation;
        _ = SaveQwenSettingsAfterDelayAsync(cancellation);
    }

    private async Task SaveQwenSettingsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(600, cancellation.Token);
            await SaveQwenTestSettingsAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 用户仍在输入，等待下一次防抖保存。
        }
        catch (Exception error)
        {
            QwenMappingStatus = $"保存百炼测试凭证失败：{error.Message}";
        }
        finally
        {
            if (ReferenceEquals(_qwenSettingsSaveDebounce, cancellation))
            {
                _qwenSettingsSaveDebounce = null;
            }

            cancellation.Dispose();
        }
    }

    private Task SaveQwenTestSettingsAsync(CancellationToken cancellationToken) =>
        _qwenTestSettingsStore.SaveAsync(new QwenTestSettings(QwenApiKey), cancellationToken);

    private void CancelPendingQwenSettingsSave()
    {
        var pending = Interlocked.Exchange(ref _qwenSettingsSaveDebounce, null);
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
        OzonAttributes.Clear();
        OzonSchemaJson = "尚未读取 Ozon Schema。";
        ResetQwenMappingOutput("Ozon 凭证和 Schema 已清除，无法生成 AI 输入 JSON。");
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

    public async Task ClearTemporaryQwenCredentialsAsync()
    {
        CancelPendingQwenSettingsSave();
        _isRestoringQwenSettings = true;
        try
        {
            QwenApiKey = string.Empty;
        }
        finally
        {
            _isRestoringQwenSettings = false;
        }

        try
        {
            await _qwenTestSettingsStore.ClearCredentialsAsync(CancellationToken.None);
            QwenMappingStatus = "已从Windows Credential Manager删除百炼API Key。";
        }
        catch (Exception error)
        {
            QwenMappingStatus = $"界面凭证已清空，但删除百炼凭证失败：{error.Message}";
        }

        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunQwenMapping() =>
        !IsRunningQwenMapping && !IsBusy && !IsLoadingOzonSchema &&
        _ozonSchema is not null && _latestFieldMatchingInput is not null &&
        SelectedOzonCategory?.DescriptionCategoryId == _ozonSchema.DescriptionCategoryId &&
        SelectedOzonType?.TypeId == _ozonSchema.TypeId;

    [RelayCommand(CanExecute = nameof(CanRunQwenMapping))]
    private Task RunQwenMappingAsync()
    {
        if (!CanRunQwenMapping() || _latestFieldMatchingInput is null) return Task.CompletedTask;
        ResetProductMappingPreview("正在整理当前商品的 AI 输入 JSON…");
        try
        {
            var input = ProductMappingInputBuilder.Create(_latestFieldMatchingInput);
            ShowSkuIdentityPlan(input.Request);
            FieldMatchingInputJson = JsonSerializer.Serialize(
                QwenProductMappingTransport.Create(input.Request), ProductMappingJson.IndentedOptions);
            QwenRequestJson = FieldMatchingInputJson;
            foreach (var issue in input.SourceIssues) ProductMappingIssues.Add(issue);
            QwenRawResponseJson = "AI 调用已暂停，本次没有发送请求。";
            QwenDictionaryStatus = "仅在本地生成 AI 输入 JSON；未调用 Qwen，也未查询 Ozon 字典。";
            FieldMatchingFinalOutputJson = "AI 调用已暂停；本次只生成输入 JSON，没有映射或组合结果。";
            FieldMatchingFinalStatus = $"AI 输入 JSON 已生成：{input.Request.Facts.Count} 条事实、" +
                $"{input.Request.Skus.Count} 个 SKU 组合、{input.Request.Attributes.Count} 个目标字段。请检查“AI 输入 JSON”。";
            QwenMappingStatus = FieldMatchingFinalStatus;
            SelectedFieldMatchingTabIndex = 5;
        }
        catch (Exception error)
        {
            QwenMappingStatus = FieldMatchingFinalStatus = $"生成 AI 输入 JSON 失败：{error.Message}";
            ProductMappingIssues.Add(new("error", "input.invalid", "product", null, error.Message));
        }
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRunQwenMapping))]
    private async Task RunRuleMappingAsync()
    {
        if (!CanRunQwenMapping() || _latestFieldMatchingInput is null) return;
        ResetProductMappingPreview("正在执行规则匹配…");
        using var cancellation = new CancellationTokenSource();
        _ruleMappingCancellation = cancellation;
        var source = _latestFieldMatchingInput;
        IsRunningQwenMapping = true;
        try
        {
            var input = ProductMappingInputBuilder.Create(source);
            ShowSkuIdentityPlan(input.Request);
            var run = await _productMappingRunner.RunAsync(input,
                new OzonTemporaryCredentials(OzonClientId, OzonApiKey), null, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(source, _latestFieldMatchingInput)) return;
            foreach (var row in run.Validation.Rows) ProductMappingRows.Add(row);
            foreach (var issue in run.Validation.Issues) ProductMappingIssues.Add(issue);
            FieldMatchingFinalOutputJson = JsonSerializer.Serialize(new
            {
                mapping = run,
                categoryRules = _categoryRuleEngine.Match(run.Request),
                ozonImportDraft = OzonFieldCompositionEngine.Compose(run.Request, run.Response, run.Validation),
            }, ProductMappingJson.IndentedOptions);
            QwenRawResponseJson = "字段匹配已禁用 AI/Qwen 补齐。";
            QwenDictionaryStatus = "仅按确定性规则查询字典并接受唯一精确命中；其余留空。";
            FieldMatchingFinalStatus = $"规则匹配完成：{run.Validation.UnresolvedRequiredCount} 个必填项待人工处理。当前结果只读，人工编辑保存入口尚未实现。";
            QwenMappingStatus = FieldMatchingFinalStatus;
            SelectedFieldMatchingTabIndex = 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            FieldMatchingFinalStatus = "规则匹配失败，请检查输入与 Ozon 配置后重试。";
        }
        finally
        {
            if (ReferenceEquals(_ruleMappingCancellation, cancellation)) _ruleMappingCancellation = null;
            IsRunningQwenMapping = false;
        }
    }

    private bool CanCancelProductMapping() => IsRunningQwenMapping;

    [RelayCommand(CanExecute = nameof(CanCancelProductMapping))]
    private void CancelProductMapping()
    {
        ResetProductMappingPreview("本次映射已取消。");
        QwenMappingStatus = "本次映射已取消。";
    }

    private void ResetProductMappingPreview(string status)
    {
        _ruleMappingCancellation?.Cancel();
        ProductMappingRows.Clear();
        ProductMappingIssues.Clear();
        ProductSkuIdentityRows.Clear();
        ProductSkuPlanStatus = "等待 SKU 采集结果。";
        QwenRequestJson = "尚未生成本次 AI 请求。";
        QwenRawResponseJson = "AI 调用已暂停。";
        QwenDictionaryStatus = "AI 调用已暂停；当前不查询字典。";
        FieldMatchingFinalOutputJson = "AI 调用已暂停；当前只预览输入 JSON。";
        FieldMatchingFinalStatus = status;
    }

    private void RefreshFieldMatchingInput()
    {
        ResetProductMappingPreview("商品或 Schema 已更新，请重新生成 AI 输入 JSON。");
        FieldMatchingFacts.Clear();
        if (SelectedFieldMatchingDetail is null)
        {
            _latestFieldMatchingInput = null;
            ClearFieldMatchingPreview("当前没有可供字段匹配的详情结果。");
            return;
        }

        var categoryPath = SelectedOzonCategory is not null && SelectedOzonType is not null
            ? $"{SelectedOzonCategory.DisplayName} > {SelectedOzonType.Name}"
            : null;
        var input = FieldMatchingInputBuilder.Create(
            string.IsNullOrWhiteSpace(_latestCollectionId) ? "current-session" : _latestCollectionId,
            $"preview-{SelectedFieldMatchingDetail.ItemPosition:000}",
            SelectedFieldMatchingDetail,
            _ozonSchema,
            categoryPath,
            _ozonSchema is not null && categoryPath is not null);
        _latestFieldMatchingInput = input;
        var cleaned = CleanedProductBuilder.Create(input);
        CleanedProductJson = JsonSerializer.Serialize(
            cleaned, ProductMappingJson.IndentedOptions);
        foreach (var fact in input.Source.Facts.Where(f => f.Kind is "title" or "attribute")) FieldMatchingFacts.Add(fact);

        FieldMatchingInputJson = "请先读取当前 Ozon Schema，再生成 AI 输入 JSON。";
        if (input.Target.Attributes.Count > 0)
        {
            try
            {
                var preview = ProductMappingInputBuilder.Create(input, $"preview-v2-{input.ProductRef.OfferId}");
                ShowSkuIdentityPlan(preview.Request);
                FieldMatchingInputJson = JsonSerializer.Serialize(
                    QwenProductMappingTransport.Create(preview.Request), ProductMappingJson.IndentedOptions);
                FieldMatchingFacts.Clear();
                foreach (var fact in preview.Request.Facts)
                    FieldMatchingFacts.Add(new(fact.FactId, fact.ScopeKey, fact.Label, fact.Value, fact.Source, fact.SourcePath));
                foreach (var issue in preview.SourceIssues) ProductMappingIssues.Add(issue);
            }
            catch (ArgumentException error)
            {
                ProductMappingIssues.Add(new("error", "input.invalid", "product", null, error.Message));
            }
        }
        var targetText = input.Target.Attributes.Count == 0
            ? "尚未读取 Ozon Schema"
            : $"Ozon 目标字段 {input.Target.Attributes.Count} 项（必填 {input.Target.Attributes.Count(item => item.IsRequired)}）";
        FieldMatchingStatus =
            $"商品 #{input.ProductRef.ItemPosition} · 数据清洗：确认 {cleaned.ConfirmedFacts.Count} 条、待确认 {cleaned.UncertainFacts.Count} 条、规格组合 {cleaned.SkuCombinations.Count} 个 · " +
            $"{input.Source.Facts.Count} 条属性事实 · " +
            $"{input.Source.Media.Count} 张图片 · {input.Source.PriceEvidence.Count} 条价格证据 · " +
            $"{input.Source.SkuEvidence.Count} 条 SKU 证据 · " +
            $"{input.Source.SkuCombinations.Count} 个 SKU 组合（{input.Source.SkuMatrixStatus}）；{targetText}。";
    }

    private void ShowSkuIdentityPlan(ProductMappingRequest request)
    {
        ProductSkuIdentityRows.Clear();
        foreach (var sku in request.Skus)
            ProductSkuIdentityRows.Add(new(
                sku.VariantKey,
                sku.MerchantSku,
                sku.IdentityStrategy,
                sku.SourceCombinationKey,
                sku.SourceSkuId,
                sku.Options));
        ProductSkuPlanStatus = request.Skus.Count == 0
            ? $"商品组 {request.ProductGroupKey}：没有 SKU 组合，不生成变体身份。"
            : $"商品组 {request.ProductGroupKey}：已为 {request.Skus.Count} 个 SKU 组合生成稳定商家 SKU。";
    }

    private void ClearFieldMatchingPreview(string status)
    {
        ResetProductMappingPreview(status);
        FieldMatchingFacts.Clear();
        _latestFieldMatchingInput = null;
        CleanedProductJson = "选择一个已采集的商品以查看数据清洗结果。";
        FieldMatchingInputJson = "尚未生成字段匹配输入。";
        FieldMatchingFinalOutputJson = "尚未生成统一最终输出。";
        FieldMatchingFinalStatus = "等待字段匹配输入和引擎计划。";
        FieldMatchingStatus = status;
    }

    private static DetailFactSnapshotDto? CreateDetailSnapshot(
        DetailCollectionResultDto? detail,
        string? fallbackCapturedAt = null) =>
        detail is null
            ? null
            : new DetailFactSnapshotDto(
                detail.FinalUrl ?? detail.DetailUrl ?? string.Empty,
                detail.CapturedAt ?? fallbackCapturedAt ?? DateTimeOffset.UtcNow.ToString("O"),
                detail.PageTitle,
                detail.Facts,
                detail.Diagnostics,
                detail.Raw);

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
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
    }

    partial void OnQwenApiKeyChanged(string value)
    {
        ResetProductMappingPreview("AI 凭证已变化，请重新执行映射。");
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
        ScheduleQwenSettingsSave();
        RefreshQwenReadinessStatus();
    }

    partial void OnIsRunningQwenMappingChanged(bool value)
    {
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
        CancelProductMappingCommand.NotifyCanExecuteChanged();
    }

    partial void OnOzonClientIdChanged(string value)
    {
        ResetProductMappingPreview("Ozon 凭证已变化，请重新执行映射。");
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnOzonApiKeyChanged(string value)
    {
        ResetProductMappingPreview("Ozon 凭证已变化，请重新执行映射。");
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
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
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnSelectedFieldMatchingDetailChanged(DetailCollectionResultDto? value)
    {
        _latestDetailSnapshot = CreateDetailSnapshot(value);
        RefreshFieldMatchingInput();
        ResetQwenMappingOutput(value is null
            ? "请选择一个详情商品后再生成 AI 输入 JSON。"
            : "字段匹配商品已切换；请基于当前商品重新生成 AI 输入 JSON。");
        RefreshQwenReadinessStatus();
    }

    partial void OnIsLoadingOzonSchemaChanged(bool value)
    {
        if (value) ResetProductMappingPreview("正在刷新 Schema，请完成后重新映射。");
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
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
        ResetQwenMappingOutput("Ozon 品类或类型已变化，请重新读取 Schema 后再生成 AI 输入 JSON。");
        RefreshFieldMatchingInput();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
    }

    private void ResetQwenMappingOutput(string status)
    {
        QwenRequestJson = "尚未生成Qwen映射请求。";
        QwenRawResponseJson = "AI 调用已暂停。";
        QwenMappingStatus = status;
        QwenDictionaryStatus = "AI 调用已暂停；当前不查询字典。";
        ResetProductMappingPreview(status);
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        RunRuleMappingCommand.NotifyCanExecuteChanged();
    }

    private void RefreshQwenReadinessStatus()
    {
        if (IsRunningQwenMapping)
        {
            return;
        }

        QwenMappingStatus = _ozonSchema is null
            ? "AI 调用已暂停；请先读取当前 Ozon Schema。"
            : _latestDetailSnapshot is null
                ? "AI 调用已暂停；请先采集 1688 详情事实。"
                : "AI 调用已暂停；可以在字段匹配页生成并检查输入 JSON，无需百炼 API Key。";
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
