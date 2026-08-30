using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using AutoMagic.Application.ExchangeRates;
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
    private readonly Dispatcher _dispatcher;
    private bool _initialized;

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

    public MainViewModel(
        ISearchBridge searchBridge,
        IExchangeRateService exchangeRateService)
    {
        _searchBridge = searchBridge;
        _exchangeRateService = exchangeRateService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _statusText = GetConnectionText(searchBridge.IsExtensionConnected);
        _searchBridge.ConnectionChanged += OnConnectionChanged;
    }

    public ObservableCollection<ProductItemDto> Products { get; } = [];

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
        await RefreshExchangeRatesAsync();
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

        try
        {
            var result = await _searchBridge.SearchAsync(
                Keyword,
                60,
                costRange.ProcurementMinimumCny,
                costRange.ProcurementMaximumCny,
                SelectedSortMode,
                CancellationToken.None);
            foreach (var item in result.Items)
            {
                Products.Add(item);
            }

            ResultCount = result.Count;
            RawJson = JsonSerializer.Serialize(result, BridgeJson.IndentedOptions);
            StatusText = $"采集完成：已收到 {result.Count} 条商品数据。";
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

    partial void OnIsBusyChanged(bool value) => SearchCommand.NotifyCanExecuteChanged();

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
