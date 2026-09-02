using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using AutoMagic.Application.ExchangeRates;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Application.Search;
using AutoMagic.Contracts.Protocol;
using AutoMagic.Domain.Pricing;
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
    private readonly Dispatcher _dispatcher;
    private bool _initialized;
    private OzonCategorySchema? _ozonSchema;
    private DetailFactSnapshotDto? _latestDetailSnapshot;
    private int _ozonSchemaRequestRevision;
    private CancellationTokenSource? _ozonSettingsSaveDebounce;
    private bool _isRestoringOzonSettings;
    private CancellationTokenSource? _qwenSettingsSaveDebounce;
    private bool _isRestoringQwenSettings;
    private SemanticMappingResponse? _latestQwenMappingResponse;

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

    public MainViewModel(
        ISearchBridge searchBridge,
        IExchangeRateService exchangeRateService,
        IOzonSchemaService ozonSchemaService,
        ILocalOzonCategoryCatalog ozonCategoryCatalog,
        IOzonTestSettingsStore ozonTestSettingsStore,
        IOzonDictionaryService ozonDictionaryService,
        IQwenSemanticMappingService qwenMappingService,
        IQwenTestSettingsStore qwenTestSettingsStore)
    {
        _searchBridge = searchBridge;
        _exchangeRateService = exchangeRateService;
        _ozonSchemaService = ozonSchemaService;
        _ozonCategoryCatalog = ozonCategoryCatalog;
        _ozonTestSettingsStore = ozonTestSettingsStore;
        _ozonDictionaryService = ozonDictionaryService;
        _qwenMappingService = qwenMappingService;
        _qwenTestSettingsStore = qwenTestSettingsStore;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _statusText = GetConnectionText(searchBridge.IsExtensionConnected);
        _searchBridge.ConnectionChanged += OnConnectionChanged;
    }

    public ObservableCollection<ProductItemDto> Products { get; } = [];

    public ObservableCollection<OzonAttributeDefinition> OzonAttributes { get; } = [];

    public ObservableCollection<AttributeResolution> CoverageAttributes { get; } = [];

    public ObservableCollection<OzonCategoryOption> OzonCategoryOptions { get; } = [];

    public ObservableCollection<OzonTypeOption> OzonTypeOptions { get; } = [];

    public ObservableCollection<SemanticTargetMapping> QwenMappings { get; } = [];

    public ObservableCollection<SemanticMappingValidationIssue> QwenValidationIssues { get; } = [];

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
            foreach (var item in result.Items)
            {
                Products.Add(item);
            }

            ResultCount = result.Count;
            RawJson = JsonSerializer.Serialize(result, BridgeJson.IndentedOptions);
            _latestDetailSnapshot = result.DetailSnapshot;
            RefreshAttributeCoverage();
            RefreshQwenReadinessStatus();
            RunQwenMappingCommand.NotifyCanExecuteChanged();
            var factStatus = result.DetailSnapshot is null
                ? "第2条详情事实未返回"
                : $"第2条详情提取 {result.DetailSnapshot.Facts.Count} 项事实";
            var unresolvedDetailUrlCount = GetUnresolvedDetailUrlCount(result.Diagnostics);
            var detailUrlStatus = unresolvedDetailUrlCount > 0
                ? $"{unresolvedDetailUrlCount} 条广告商品详情地址尚未解析；"
                : string.Empty;
            StatusText =
                $"采集完成：已收到 {result.Count} 条商品数据；{detailUrlStatus}{factStatus}。";
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
        _ozonSchema is not null &&
        _latestDetailSnapshot is not null &&
        SelectedOzonCategory is not null &&
        SelectedOzonType is not null;

    [RelayCommand(CanExecute = nameof(CanRunQwenMapping))]
    private async Task RunQwenMappingAsync()
    {
        if (_ozonSchema is null ||
            _latestDetailSnapshot is null ||
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
        QwenRawResponseJson = "等待百炼返回映射结果…";
        QwenMappingStatus = $"正在调用 {QwenMappingRuntime.ModelId} 执行语义映射…";
        try
        {
            await SaveQwenTestSettingsAsync(CancellationToken.None);
            var request = SemanticMappingRequestFactory.CreateRequiredAttributeRequest(
                $"automagic-{Guid.NewGuid():N}",
                $"{SelectedOzonCategory.DisplayName} > {SelectedOzonType.Name}",
                _ozonSchema,
                _latestDetailSnapshot);
            QwenRequestJson = JsonSerializer.Serialize(request, SemanticMappingJson.IndentedOptions);

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

        QwenDictionaryStatus = "首次映射已通过本地校验；可以搜索 Ozon 参考值并再次复核。";
            }
            else
            {
                _latestQwenMappingResponse = null;
                QwenDictionaryStatus = "首次映射未通过本地校验，暂不读取字典候选。";
            }

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
            var candidates = await LoadDictionaryCandidatesAsync(_latestQwenMappingResponse);
            var request = SemanticMappingRequestFactory.CreateRequiredAttributeRequest(
                $"automagic-{Guid.NewGuid():N}",
                $"{SelectedOzonCategory.DisplayName} > {SelectedOzonType.Name}",
                _ozonSchema,
                _latestDetailSnapshot,
                candidates);
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

    private static bool NeedsDictionaryLookup(SemanticTargetMapping mapping) =>
        mapping.DictionaryResolutionRequired &&
        mapping.SelectedDictionaryValueIds.Count == 0 &&
        mapping.CandidateTextValues.Count > 0;

    private void RefreshAttributeCoverage()
    {
        CoverageAttributes.Clear();
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
        foreach (var attribute in report.Attributes)
        {
            CoverageAttributes.Add(attribute);
        }

        CoverageJson = JsonSerializer.Serialize(report, BridgeJson.IndentedOptions);
        CoverageStatus = report.IsReadyForSubmission
            ? $"必填属性已全部就绪：{report.ReadyRequiredCount}/{report.RequiredCount}。"
            : $"必填 {report.RequiredCount} 项：可直接使用 {report.ReadyRequiredCount}，待 Ozon 字典解析 {report.DictionaryRequiredCount}，待人工确认 {report.ReviewRequiredCount}，缺失 {report.MissingRequiredCount}。";
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
        ResolveQwenDictionariesCommand.NotifyCanExecuteChanged();
        ScheduleOzonSettingsSave();
    }

    partial void OnOzonApiKeyChanged(string value)
    {
        LoadOzonSchemaCommand.NotifyCanExecuteChanged();
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
        QwenDictionaryStatus = "首次映射通过校验后，可搜索 Ozon 参考值并再次复核。";
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

    private static string GetConnectionText(bool connected) => connected
        ? "Chrome 插件已连接，可以开始搜索。"
        : "等待 Chrome 插件连接。请先确认原生消息宿主已注册、插件已启用。";

    private static string FormatRange(decimal minimum, decimal maximum, string unit) =>
        $"{minimum:0.00} - {maximum:0.00} {unit}";
}

public sealed record ProductSortOption(string Value, string Label);
