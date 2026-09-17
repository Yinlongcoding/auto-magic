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
    private readonly IOzonDictionaryService _ozonDictionaryService;
    private readonly IQwenSemanticMappingService _qwenMappingService;
    private readonly IQwenTestSettingsStore _qwenTestSettingsStore;
    private readonly CollectionSnapshotStore _collectionSnapshotStore;
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
    private SemanticMappingResponse? _latestQwenMappingResponse;
    private FieldMatchingInput? _latestFieldMatchingInput;
    private AttributeCoverageReport? _latestCoverageReport;
    private FieldMatchingEnginePlan? _latestFieldMatchingPlan;
    private IReadOnlyCollection<FieldConversionDecision> _latestConversionResults = [];
    private IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>> _latestDictionaryCandidates =
        new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>();

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
    private string _coverageStatus = "请先读取 Ozon Schema，再执行一次商品搜索。";

    [ObservableProperty]
    private string _coverageJson = "尚未生成属性覆盖报告。";

    [ObservableProperty]
    private string _qwenApiKey = string.Empty;

    [ObservableProperty]
    private bool _isRunningQwenMapping;

    [ObservableProperty]
    private bool _isResolvingQwenDictionaries;

    [ObservableProperty]
    private string _qwenMappingStatus = "请先准备 Ozon Schema、1688详情事实和百炼API Key。";

    [ObservableProperty]
    private string _qwenRequestJson = "尚未生成Qwen映射请求。";

    [ObservableProperty]
    private string _qwenRawResponseJson = "尚未调用Qwen。";

    [ObservableProperty]
    private string _qwenDictionaryStatus = "首次映射通过校验后，可搜索 Ozon 参考值并再次复核。";

    [ObservableProperty]
    private DetailCollectionResultDto? _selectedFieldMatchingDetail;

    [ObservableProperty]
    private string _fieldMatchingStatus = "完成一次详情采集后，可以在这里审阅匹配输入。";

    [ObservableProperty]
    private string _fieldMatchingInputJson = "尚未生成字段匹配输入。";

    [ObservableProperty]
    private string _fieldMatchingPlanJson = "尚未生成通用匹配引擎计划。";

    [ObservableProperty]
    private string _fieldMatchingFinalOutputJson = "尚未生成统一最终输出。";

    [ObservableProperty]
    private string _fieldMatchingFinalStatus = "等待字段匹配输入和引擎计划。";

    [ObservableProperty]
    private string _conversionRuleStatus = "等待识别可用的转换规则。";

    public MainViewModel(
        ISearchBridge searchBridge,
        IExchangeRateService exchangeRateService,
        IOzonSchemaService ozonSchemaService,
        ILocalOzonCategoryCatalog ozonCategoryCatalog,
        IOzonTestSettingsStore ozonTestSettingsStore,
        IOzonDictionaryService ozonDictionaryService,
        IQwenSemanticMappingService qwenMappingService,
        IQwenTestSettingsStore qwenTestSettingsStore,
        CollectionSnapshotStore collectionSnapshotStore)
    {
        _searchBridge = searchBridge;
        _exchangeRateService = exchangeRateService;
        _ozonSchemaService = ozonSchemaService;
        _ozonCategoryCatalog = ozonCategoryCatalog;
        _ozonTestSettingsStore = ozonTestSettingsStore;
        _ozonDictionaryService = ozonDictionaryService;
        _qwenMappingService = qwenMappingService;
        _qwenTestSettingsStore = qwenTestSettingsStore;
        _collectionSnapshotStore = collectionSnapshotStore;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _statusText = GetConnectionText(searchBridge.IsExtensionConnected);
        _searchBridge.ConnectionChanged += OnConnectionChanged;
        _searchBridge.SearchProgressChanged += OnSearchProgressChanged;
    }

    public ObservableCollection<ProductItemDto> Products { get; } = [];

    public ObservableCollection<OzonAttributeDefinition> OzonAttributes { get; } = [];

    public ObservableCollection<AttributeResolution> CoverageAttributes { get; } = [];

    public ObservableCollection<OzonCategoryOption> OzonCategoryOptions { get; } = [];

    public ObservableCollection<OzonTypeOption> OzonTypeOptions { get; } = [];

    public ObservableCollection<SemanticTargetMapping> QwenMappings { get; } = [];

    public ObservableCollection<SemanticMappingValidationIssue> QwenValidationIssues { get; } = [];

    public ObservableCollection<DetailCollectionResultDto> FieldMatchingProducts { get; } = [];

    public ObservableCollection<FieldMatchingSourceFact> FieldMatchingFacts { get; } = [];

    public ObservableCollection<FieldMatchingMediaEvidence> FieldMatchingMedia { get; } = [];

    public ObservableCollection<FieldMatchingTextEvidence> FieldMatchingPriceEvidence { get; } = [];

    public ObservableCollection<FieldMatchingTextEvidence> FieldMatchingSkuEvidence { get; } = [];

    public ObservableCollection<FieldMatchingTargetAttribute> FieldMatchingTargets { get; } = [];

    public ObservableCollection<FieldMatchingEngineAttribute> FieldMatchingEngineAttributes { get; } = [];

    public ObservableCollection<FieldMatchingFinalTargetMapping> FieldMatchingFinalMappings { get; } = [];

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
        RefreshAttributeCoverage();
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
        FieldMatchingProducts.Clear();
        foreach (var detail in _latestDetailResults) FieldMatchingProducts.Add(detail);
        SelectedFieldMatchingDetail = _latestDetailResults.FirstOrDefault(item =>
            string.Equals(item.Status, "success", StringComparison.OrdinalIgnoreCase))
            ?? _latestDetailResults.FirstOrDefault();
        _latestDetailSnapshot = CreateDetailSnapshot(SelectedFieldMatchingDetail, result.CapturedAt)
            ?? result.DetailSnapshot;
        RefreshFieldMatchingInput();
        RefreshAttributeCoverage();
        RefreshQwenReadinessStatus();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
    }

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
        CoverageAttributes.Clear();
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
            RefreshAttributeCoverage();
            ResetQwenMappingOutput("Schema和详情事实就绪后，可以执行AI语义映射。");
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
            CoverageStatus = "Schema 不可用，无法生成覆盖报告。";
            CoverageJson = string.Empty;
            ResetQwenMappingOutput("Ozon Schema不可用，无法执行AI语义映射。");
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
                ? "尚未保存百炼API Key；准备好Schema和详情事实后可执行AI映射。"
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
        CoverageAttributes.Clear();
        CoverageJson = "尚未生成属性覆盖报告。";
        CoverageStatus = "请先读取 Ozon Schema，再执行一次商品搜索。";
        ResetQwenMappingOutput("Ozon凭证和Schema已清除，无法执行AI语义映射。");
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
    }

    private bool CanRunQwenMapping() =>
        !IsRunningQwenMapping &&
        !IsResolvingQwenDictionaries &&
        !IsBusy &&
        !string.IsNullOrWhiteSpace(QwenApiKey) &&
        !string.IsNullOrWhiteSpace(OzonClientId) &&
        !string.IsNullOrWhiteSpace(OzonApiKey) &&
        _ozonSchema is not null &&
        _latestDetailSnapshot is not null &&
        _latestFieldMatchingInput is not null &&
        _latestCoverageReport is not null &&
        SelectedOzonCategory is not null &&
        SelectedOzonType is not null;

    [RelayCommand(CanExecute = nameof(CanRunQwenMapping))]
    private async Task RunQwenMappingAsync()
    {
        if (_ozonSchema is null ||
            _latestDetailSnapshot is null ||
            _latestFieldMatchingInput is null ||
            _latestCoverageReport is null ||
            SelectedOzonCategory is null ||
            SelectedOzonType is null)
        {
            QwenMappingStatus = "请先读取Ozon Schema并完成一次包含详情事实的商品搜索。";
            return;
        }

        IsRunningQwenMapping = true;
        _latestQwenMappingResponse = null;
        QwenMappings.Clear();
        QwenValidationIssues.Clear();
        QwenRawResponseJson = "正在准备Ozon合法字典候选…";
        QwenMappingStatus = "通用匹配引擎正在执行确定性匹配和Ozon字典查询…";
        try
        {
            await SaveQwenTestSettingsAsync(CancellationToken.None);
            var initialPlan = FieldMatchingEnginePlanBuilder.Create(
                _latestFieldMatchingInput,
                _latestCoverageReport);
            var candidates = await LoadPreQwenDictionaryCandidatesAsync(initialPlan);
            _latestDictionaryCandidates = candidates;
            var preparedPlan = FieldMatchingEnginePlanBuilder.Create(
                _latestFieldMatchingInput,
                _latestCoverageReport,
                candidates);
            _latestConversionResults = await ResolveAutomaticConversionsAsync(preparedPlan);
            PublishFieldMatchingPlan(preparedPlan);

            if (preparedPlan.QwenAttributeIds.Count == 0)
            {
                var unresolvedRequired = preparedPlan.Attributes.Count(attribute =>
                    attribute.IsRequired &&
                    attribute.Status != FieldMatchingEngineStatuses.Resolved);
                QwenRequestJson = "没有需要Qwen裁决的必填字段。";
                QwenRawResponseJson = "未调用Qwen。";
                QwenDictionaryStatus = $"AI前字典查询完成：{candidates.Count}个属性获得合法候选。";
                QwenMappingStatus = unresolvedRequired == 0
                    ? "确定性规则和字典匹配已经完成当前必填字段，无需调用Qwen。"
                    : $"当前没有可交由Qwen裁决的字段；仍有{unresolvedRequired}个必填字段等待字典、转换或平台策略。";
                return;
            }

            var qwenAttributeIds = preparedPlan.QwenAttributeIds.ToHashSet();
            var request = SemanticMappingRequestFactory.CreateFromFieldMatchingInput(
                $"automagic-{Guid.NewGuid():N}",
                _latestFieldMatchingInput,
                candidates,
                qwenAttributeIds);
            QwenRequestJson = JsonSerializer.Serialize(request, SemanticMappingJson.IndentedOptions);
            QwenRawResponseJson = "等待百炼返回受约束的语义映射结果…";
            QwenDictionaryStatus =
                $"AI前字典查询完成：{candidates.Count}个属性获得合法候选；" +
                $"Qwen仅处理{qwenAttributeIds.Count}个未解决字段。";
            QwenMappingStatus = $"正在调用 {QwenMappingRuntime.ModelId} 执行受约束语义裁决…";

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await _qwenMappingService.MapAsync(
                new QwenApiCredentials(QwenApiKey),
                request,
                timeout.Token);
            QwenRawResponseJson = result.RawContent;
            foreach (var issue in result.Validation.Issues)
            {
                QwenValidationIssues.Add(issue);
            }

            if (result.Validation.Response is { } response)
            {
                _latestQwenMappingResponse = response;
                foreach (var mapping in response.TargetMappings)
                {
                    QwenMappings.Add(mapping);
                }

                QwenDictionaryStatus += " AI响应已通过本地合同校验。";
            }
            else
            {
                _latestQwenMappingResponse = null;
                QwenDictionaryStatus += " AI响应未通过本地合同校验。";
            }
            RefreshFieldMatchingFinalOutput();

            var tokenText = result.Usage.TotalTokens > 0
                ? $"Token {result.Usage.PromptTokens}+{result.Usage.CompletionTokens}={result.Usage.TotalTokens}"
                : "服务未返回Token统计";
            QwenMappingStatus = result.Validation.IsValid
                ? $"AI映射完成并通过本地校验：{QwenMappings.Count}项；{tokenText}。尚未写入映射库。"
                : $"AI返回已收到，但本地校验失败：{QwenValidationIssues.Count}项问题；{tokenText}。结果不会写入映射库。";
        }
        catch (OperationCanceledException)
        {
            QwenRawResponseJson = string.Empty;
            QwenMappingStatus = "AI映射请求已取消或超过90秒。";
        }
        catch (Exception error)
        {
            QwenRawResponseJson = string.Empty;
            QwenMappingStatus = $"AI映射失败：{error.Message}";
        }
        finally
        {
            IsRunningQwenMapping = false;
        }
    }

    private bool CanResolveQwenDictionaries() =>
        !IsRunningQwenMapping &&
        !IsResolvingQwenDictionaries &&
        !IsBusy &&
        !string.IsNullOrWhiteSpace(QwenApiKey) &&
        !string.IsNullOrWhiteSpace(OzonClientId) &&
        !string.IsNullOrWhiteSpace(OzonApiKey) &&
        _ozonSchema is not null &&
        _latestDetailSnapshot is not null &&
        _latestQwenMappingResponse is not null &&
        _latestQwenMappingResponse.TargetMappings.Any(mapping =>
            mapping.CandidateTextValues.Count > 0 &&
            _ozonSchema.Attributes.Any(attribute =>
                attribute.Id == mapping.AttributeId && attribute.DictionaryId > 0));

    [RelayCommand(CanExecute = nameof(CanResolveQwenDictionaries))]
    private async Task ResolveQwenDictionariesAsync()
    {
        if (_ozonSchema is null ||
            _latestDetailSnapshot is null ||
            _latestFieldMatchingInput is null ||
            _latestCoverageReport is null ||
            _latestQwenMappingResponse is null ||
            SelectedOzonCategory is null ||
            SelectedOzonType is null)
        {
            QwenDictionaryStatus = "请先完成一次通过本地校验的 AI 映射。";
            return;
        }

        IsResolvingQwenDictionaries = true;
        QwenDictionaryStatus = "正在按未解决的文本候选搜索 Ozon 参考值…";
        try
        {
            var supplementalCandidates = await LoadDictionaryCandidatesAsync(_latestQwenMappingResponse);
            var candidates = _latestDictionaryCandidates
                .Concat(supplementalCandidates)
                .GroupBy(pair => pair.Key)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<SemanticDictionaryCandidate>)group
                        .SelectMany(pair => pair.Value)
                        .GroupBy(candidate => candidate.ValueId)
                        .Select(candidateGroup => candidateGroup.First())
                        .ToArray());
            _latestDictionaryCandidates = candidates;
            var preparedPlan = FieldMatchingEnginePlanBuilder.Create(
                _latestFieldMatchingInput,
                _latestCoverageReport,
                candidates);
            _latestQwenMappingResponse = null;
            PublishFieldMatchingPlan(preparedPlan);
            var qwenAttributeIds = preparedPlan.QwenAttributeIds.ToHashSet();
            if (qwenAttributeIds.Count == 0)
            {
                QwenDictionaryStatus = "补充字典完成，当前没有需要Qwen再次裁决的字段。";
                return;
            }

            var request = SemanticMappingRequestFactory.CreateFromFieldMatchingInput(
                $"automagic-{Guid.NewGuid():N}",
                _latestFieldMatchingInput,
                candidates,
                qwenAttributeIds);
            QwenRequestJson = JsonSerializer.Serialize(request, SemanticMappingJson.IndentedOptions);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await _qwenMappingService.MapAsync(
                new QwenApiCredentials(QwenApiKey),
                request,
                timeout.Token);
            QwenRawResponseJson = result.RawContent;
            QwenMappings.Clear();
            QwenValidationIssues.Clear();
            foreach (var issue in result.Validation.Issues)
            {
                QwenValidationIssues.Add(issue);
            }

            if (result.Validation.Response is { } response)
            {
                _latestQwenMappingResponse = response;
                foreach (var mapping in response.TargetMappings)
                {
                    QwenMappings.Add(mapping);
                }
            }
            RefreshFieldMatchingFinalOutput();

            var candidateAttributeCount = candidates.Count;
            var tokenText = result.Usage.TotalTokens > 0
                ? $"Token {result.Usage.PromptTokens}+{result.Usage.CompletionTokens}={result.Usage.TotalTokens}"
                : "服务未返回Token统计";
            QwenDictionaryStatus = result.Validation.IsValid
                ? $"Ozon 参考值搜索并复核完成：已提供 {candidateAttributeCount} 个属性的候选；{tokenText}。"
                : $"Ozon 参考值复核返回但未通过本地校验：{QwenValidationIssues.Count}项问题；{tokenText}。";
        }
        catch (OperationCanceledException)
        {
            QwenDictionaryStatus = "字典读取或复核请求已取消或超过120秒。";
        }
        catch (Exception error)
        {
            QwenDictionaryStatus = $"字典候选解析失败：{error.Message}";
        }
        finally
        {
            IsResolvingQwenDictionaries = false;
            ResolveQwenDictionariesCommand.NotifyCanExecuteChanged();
            RunQwenMappingCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>> LoadDictionaryCandidatesAsync(
        SemanticMappingResponse response)
    {
        var candidates = new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>();
        var attributes = _ozonSchema!.Attributes.ToDictionary(attribute => attribute.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        foreach (var mapping in response.TargetMappings)
        {
            if (!attributes.TryGetValue(mapping.AttributeId, out var attribute) ||
                attribute.DictionaryId <= 0 ||
                !NeedsDictionaryLookup(mapping))
            {
                continue;
            }

            var values = new List<OzonDictionaryValue>();
            foreach (var candidateText in mapping.CandidateTextValues)
            {
                values.AddRange(await _ozonDictionaryService.SearchAttributeValuesAsync(
                    new OzonTemporaryCredentials(OzonClientId, OzonApiKey),
                    _ozonSchema.DescriptionCategoryId,
                    _ozonSchema.TypeId,
                    attribute.Id,
                    candidateText,
                    timeout.Token));
            }

            var narrowed = SemanticDictionaryCandidateResolver.Resolve(
                mapping.CandidateTextValues,
                values
                    .GroupBy(value => value.ValueId)
                    .Select(group => group.First())
                    .ToArray());
            if (narrowed.Count > 0)
            {
                candidates[attribute.Id] = narrowed;
            }
        }

        return candidates;
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>> LoadPreQwenDictionaryCandidatesAsync(
        FieldMatchingEnginePlan plan)
    {
        var result = new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>();
        var failures = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var credentials = new OzonTemporaryCredentials(OzonClientId, OzonApiKey);

        foreach (var lookup in plan.DictionaryLookups)
        {
            try
            {
                IReadOnlyList<OzonDictionaryValue> values;
                if (lookup.Strategy == DictionaryLookupStrategies.LoadAllValues)
                {
                    values = await _ozonDictionaryService.GetAttributeValuesAsync(
                        credentials,
                        plan.Target.DescriptionCategoryId!.Value,
                        plan.Target.TypeId!.Value,
                        lookup.AttributeId,
                        "DEFAULT",
                        timeout.Token);
                }
                else
                {
                    var found = new List<OzonDictionaryValue>();
                    foreach (var text in lookup.SearchTexts.Take(60))
                    {
                        found.AddRange(await _ozonDictionaryService.SearchAttributeValuesAsync(
                            credentials,
                            plan.Target.DescriptionCategoryId!.Value,
                            plan.Target.TypeId!.Value,
                            lookup.AttributeId,
                            text,
                            timeout.Token));
                    }

                    values = found;
                }

                var candidates = values
                    .Where(value => value.ValueId > 0 && !string.IsNullOrWhiteSpace(value.Value))
                    .GroupBy(value => value.ValueId)
                    .Select(group => group.First())
                    .Take(100)
                    .Select(value => new SemanticDictionaryCandidate(
                        value.ValueId, value.Value, value.Info, value.Picture))
                    .ToArray();
                if (candidates.Length > 0)
                {
                    result[lookup.AttributeId] = candidates;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                failures.Add($"{lookup.AttributeName}({lookup.AttributeId})：{error.Message}");
            }
        }

        QwenDictionaryStatus = failures.Count == 0
            ? $"AI前字典查询完成：{result.Count}/{plan.DictionaryLookups.Count}个属性获得候选。"
            : $"AI前字典查询部分完成：{result.Count}/{plan.DictionaryLookups.Count}个属性获得候选；" +
              $"{failures.Count}项失败，将保持dictionary_pending。";
        return result;
    }

    private async Task<IReadOnlyCollection<FieldConversionDecision>> ResolveAutomaticConversionsAsync(
        FieldMatchingEnginePlan plan)
    {
        if (_latestFieldMatchingInput is null ||
            !plan.Attributes.Any(attribute =>
                attribute.AttributeId == ConversionRuleSelector.RussianSizeAttributeId &&
                attribute.Status == FieldMatchingEngineStatuses.ConversionRequired))
        {
            ConversionRuleStatus = "当前目标字段不需要已支持的自动转换规则。";
            return [];
        }

        var selection = ConversionRuleSelector.SelectRussianSize(_latestFieldMatchingInput);
        if (!selection.IsSelected || selection.RuleSetId is null)
        {
            ConversionRuleStatus = $"俄罗斯尺码：{selection.Reason}";
            return [];
        }

        var batch = RussianSizeRuleCatalog.ConvertMany(selection.RuleSetId, selection.SourceOptions);
        if (!batch.Options.Any(option => option.Conversion.IsMapped))
        {
            var unresolved = RussianSizeConversionDecisionFactory.Create(
                selection.AttributeId, selection.SourceFactIds, batch, [], true);
            ConversionRuleStatus = $"俄罗斯尺码转换待复核：{unresolved.Reason}";
            return [unresolved];
        }

        try
        {
            var convertedTexts = batch.Options
                .SelectMany(option => option.Conversion.CandidateRussianValues)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var values = await _ozonDictionaryService.GetAttributeValuesAsync(
                new OzonTemporaryCredentials(OzonClientId, OzonApiKey),
                plan.Target.DescriptionCategoryId!.Value,
                plan.Target.TypeId!.Value,
                selection.AttributeId,
                "DEFAULT",
                timeout.Token);
            var candidates = SemanticDictionaryCandidateResolver.Resolve(convertedTexts, values, 100);
            var decision = RussianSizeConversionDecisionFactory.Create(
                selection.AttributeId,
                selection.SourceFactIds,
                batch,
                candidates,
                true);
            ConversionRuleStatus = decision.Status == FieldConversionStatuses.Mapped
                ? $"俄罗斯尺码自动转换完成：{selection.RuleSetId}，{decision.Traces.Count}个可追溯尺码选项。"
                : $"俄罗斯尺码已执行规则转换，但Ozon字典解析未完成：{decision.Reason}";
            return [decision];
        }
        catch (OperationCanceledException)
        {
            var pending = RussianSizeConversionDecisionFactory.Create(
                selection.AttributeId,
                selection.SourceFactIds,
                batch,
                [],
                true);
            ConversionRuleStatus = "俄罗斯尺码规则已选定，但Ozon字典查询超时，保持待转换状态。";
            return [pending];
        }
        catch (Exception error)
        {
            var pending = RussianSizeConversionDecisionFactory.Create(
                selection.AttributeId,
                selection.SourceFactIds,
                batch,
                [],
                true);
            ConversionRuleStatus = $"俄罗斯尺码规则已选定，但字典解析失败：{error.Message}";
            return [pending];
        }
    }

    private static bool NeedsDictionaryLookup(SemanticTargetMapping mapping) =>
        mapping.DictionaryResolutionRequired &&
        mapping.SelectedDictionaryValueIds.Count == 0 &&
        mapping.CandidateTextValues.Count > 0;

    private void RefreshAttributeCoverage()
    {
        CoverageAttributes.Clear();
        _latestCoverageReport = null;
        if (_ozonSchema is null)
        {
            CoverageStatus = "请先读取 Ozon Schema，再执行一次商品搜索。";
            CoverageJson = "尚未生成属性覆盖报告。";
            return;
        }

        if (_latestDetailSnapshot is null)
        {
            CoverageStatus = "Schema 已就绪；请执行一次商品搜索以抓取列表第2条详情事实。";
            CoverageJson = "等待 1688 详情事实。";
            return;
        }

        var report = AttributeCoverageResolver.Resolve(_ozonSchema, _latestDetailSnapshot);
        _latestCoverageReport = report;
        foreach (var attribute in report.Attributes)
        {
            CoverageAttributes.Add(attribute);
        }

        CoverageJson = JsonSerializer.Serialize(report, BridgeJson.IndentedOptions);
        CoverageStatus = report.IsReadyForSubmission
            ? $"必填属性已全部就绪：{report.ReadyRequiredCount}/{report.RequiredCount}。"
            : $"必填 {report.RequiredCount} 项：可直接使用 {report.ReadyRequiredCount}，待 Ozon 字典解析 {report.DictionaryRequiredCount}，待规则转换 {report.ConversionRequiredCount}，待平台策略 {report.PolicyRequiredCount}，待人工确认 {report.ReviewRequiredCount}，缺失 {report.MissingRequiredCount}。";
        RefreshFieldMatchingEnginePlan();
    }

    private void RefreshFieldMatchingInput()
    {
        FieldMatchingFacts.Clear();
        FieldMatchingMedia.Clear();
        FieldMatchingPriceEvidence.Clear();
        FieldMatchingSkuEvidence.Clear();
        FieldMatchingTargets.Clear();

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
        _latestDictionaryCandidates = new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>();
        _latestConversionResults = [];
        var conversionSelection = ConversionRuleSelector.SelectRussianSize(input);
        ConversionRuleStatus = conversionSelection.Status == ConversionRuleSelectionStatuses.NotApplicable
            ? "当前Ozon Schema没有需要自动选择的俄罗斯尺码规则。"
            : conversionSelection.IsSelected
                ? $"已识别转换规则 {conversionSelection.RuleSetId}；运行引擎后执行转换与Ozon字典校验。"
                : $"转换规则待确认：{conversionSelection.Reason}";

        foreach (var fact in input.Source.Facts) FieldMatchingFacts.Add(fact);
        foreach (var media in input.Source.Media) FieldMatchingMedia.Add(media);
        foreach (var evidence in input.Source.PriceEvidence) FieldMatchingPriceEvidence.Add(evidence);
        foreach (var evidence in input.Source.SkuEvidence) FieldMatchingSkuEvidence.Add(evidence);
        foreach (var target in input.Target.Attributes) FieldMatchingTargets.Add(target);

        FieldMatchingInputJson = JsonSerializer.Serialize(input, BridgeJson.IndentedOptions);
        var targetText = input.Target.Attributes.Count == 0
            ? "尚未读取 Ozon Schema"
            : $"Ozon 目标字段 {input.Target.Attributes.Count} 项（必填 {input.Target.Attributes.Count(item => item.IsRequired)}）";
        FieldMatchingStatus =
            $"商品 #{input.ProductRef.ItemPosition} · {input.Source.Facts.Count} 条属性事实 · " +
            $"{input.Source.Media.Count} 张图片 · {input.Source.PriceEvidence.Count} 条价格证据 · " +
            $"{input.Source.SkuEvidence.Count} 条 SKU 证据 · " +
            $"{input.Source.SkuCombinations.Count} 个已观察组合（{input.Source.SkuMatrixStatus}）；{targetText}。";
        RefreshFieldMatchingEnginePlan();
    }

    private void RefreshFieldMatchingEnginePlan()
    {
        if (_latestFieldMatchingInput is null || _latestCoverageReport is null)
        {
            FieldMatchingEngineAttributes.Clear();
            FieldMatchingPlanJson = "字段匹配输入和Ozon Schema就绪后生成通用匹配引擎计划。";
            return;
        }

        var plan = FieldMatchingEnginePlanBuilder.Create(
            _latestFieldMatchingInput,
            _latestCoverageReport,
            _latestDictionaryCandidates);
        PublishFieldMatchingPlan(plan);
    }

    private void PublishFieldMatchingPlan(FieldMatchingEnginePlan plan)
    {
        _latestFieldMatchingPlan = plan;
        FieldMatchingEngineAttributes.Clear();
        foreach (var attribute in plan.Attributes)
        {
            FieldMatchingEngineAttributes.Add(attribute);
        }

        FieldMatchingPlanJson = JsonSerializer.Serialize(plan, BridgeJson.IndentedOptions);
        RefreshFieldMatchingFinalOutput();
    }

    private void RefreshFieldMatchingFinalOutput()
    {
        FieldMatchingFinalMappings.Clear();
        if (_latestFieldMatchingInput is null || _latestFieldMatchingPlan is null)
        {
            FieldMatchingFinalOutputJson = "字段匹配输入和引擎计划就绪后生成统一最终输出。";
            FieldMatchingFinalStatus = "等待字段匹配输入和引擎计划。";
            return;
        }

        try
        {
            var output = FieldMatchingFinalOutputMerger.Merge(
                _latestFieldMatchingInput,
                _latestFieldMatchingPlan,
                _latestQwenMappingResponse,
                _latestConversionResults);
            foreach (var mapping in output.TargetMappings)
            {
                FieldMatchingFinalMappings.Add(mapping);
            }

            FieldMatchingFinalOutputJson = JsonSerializer.Serialize(output, BridgeJson.IndentedOptions);
            FieldMatchingFinalStatus =
                $"统一输出：{output.Status}；必填已完成 " +
                $"{output.Validation.MappedRequiredCount}/{output.Validation.RequiredAttributeCount}，" +
                $"缺失 {output.Validation.MissingRequiredCount}，待复核 {output.Validation.ReviewRequiredCount}。";
        }
        catch (Exception error)
        {
            FieldMatchingFinalOutputJson = string.Empty;
            FieldMatchingFinalStatus = $"统一输出合并失败：{error.Message}";
        }
    }

    private void ClearFieldMatchingPreview(string status)
    {
        FieldMatchingFacts.Clear();
        FieldMatchingMedia.Clear();
        FieldMatchingPriceEvidence.Clear();
        FieldMatchingSkuEvidence.Clear();
        FieldMatchingTargets.Clear();
        FieldMatchingEngineAttributes.Clear();
        FieldMatchingFinalMappings.Clear();
        _latestFieldMatchingInput = null;
        _latestCoverageReport = null;
        _latestFieldMatchingPlan = null;
        _latestConversionResults = [];
        _latestDictionaryCandidates = new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>();
        FieldMatchingInputJson = "尚未生成字段匹配输入。";
        FieldMatchingPlanJson = "尚未生成通用匹配引擎计划。";
        FieldMatchingFinalOutputJson = "尚未生成统一最终输出。";
        FieldMatchingFinalStatus = "等待字段匹配输入和引擎计划。";
        ConversionRuleStatus = "等待识别可用的转换规则。";
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
    }

    partial void OnQwenApiKeyChanged(string value)
    {
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        ScheduleQwenSettingsSave();
        RefreshQwenReadinessStatus();
    }

    partial void OnIsRunningQwenMappingChanged(bool value) =>
        RunQwenMappingCommand.NotifyCanExecuteChanged();

    partial void OnIsResolvingQwenDictionariesChanged(bool value)
    {
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        ResolveQwenDictionariesCommand.NotifyCanExecuteChanged();
    }

    partial void OnOzonClientIdChanged(string value)
    {
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        ResolveQwenDictionariesCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnOzonApiKeyChanged(string value)
    {
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        ResolveQwenDictionariesCommand.NotifyCanExecuteChanged();
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
        RefreshAttributeCoverage();
        ResetQwenMappingOutput(value is null
            ? "请选择一个详情商品后再执行AI映射。"
            : "字段匹配商品已切换；请基于当前商品重新执行AI映射。");
        RefreshQwenReadinessStatus();
    }

    partial void OnIsLoadingOzonSchemaChanged(bool value) => LoadOzonSchemaCommand.NotifyCanExecuteChanged();

    private void ResetOzonSchemaForSelection()
    {
        Interlocked.Increment(ref _ozonSchemaRequestRevision);
        _ozonSchema = null;
        OzonAttributes.Clear();
        CoverageAttributes.Clear();
        OzonSchemaJson = "尚未读取 Ozon Schema。";
        CoverageJson = "尚未生成属性覆盖报告。";
        CoverageStatus = "当前选择尚未读取 Schema。";
        OzonSchemaStatus = SelectedOzonCategory is null
            ? "请从本地测试目录选择 Ozon 品类和商品类型。"
            : SelectedOzonType is null
                ? $"已选择品类：{SelectedOzonCategory.DisplayName}；请选择商品类型。"
                : $"已选择：{SelectedOzonCategory.DisplayName} > {SelectedOzonType.Name}；可以验证凭证并读取 Schema。";
        ResetQwenMappingOutput("Ozon品类或类型已变化，请重新读取Schema后再执行AI映射。");
        RefreshFieldMatchingInput();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
    }

    private void ResetQwenMappingOutput(string status)
    {
        _latestQwenMappingResponse = null;
        QwenMappings.Clear();
        QwenValidationIssues.Clear();
        QwenRequestJson = "尚未生成Qwen映射请求。";
        QwenRawResponseJson = "尚未调用Qwen。";
        QwenMappingStatus = status;
        QwenDictionaryStatus = "引擎会先查询Ozon字典；必要时可补充字典并再次复核。";
        RefreshFieldMatchingFinalOutput();
        RunQwenMappingCommand.NotifyCanExecuteChanged();
        ResolveQwenDictionariesCommand.NotifyCanExecuteChanged();
    }

    private void RefreshQwenReadinessStatus()
    {
        if (IsRunningQwenMapping)
        {
            return;
        }

        QwenMappingStatus = string.IsNullOrWhiteSpace(QwenApiKey)
            ? "请输入百炼API Key；凭证只会保存到Windows Credential Manager。"
            : _ozonSchema is null
                ? "百炼凭证已就绪；请先验证并读取当前Ozon Schema。"
                : _latestDetailSnapshot is null
                    ? "百炼凭证和Ozon Schema已就绪；请执行一次商品搜索以采集详情事实。"
                    : "百炼凭证、Ozon Schema和1688详情事实均已就绪，可以执行AI映射。";
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
