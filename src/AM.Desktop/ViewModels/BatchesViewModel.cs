using System.Collections.ObjectModel;
using System.ComponentModel;
using AM.Core.Pricing;
using AM.Core.Rules;
using AM.Desktop.Models;
using AM.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AM.Desktop.ViewModels;

/// <summary>
/// 采集上架：左边批次列表，右边选中批次的四个步骤页签（采集、待处理、文案、提交）。
/// 新建采集在弹窗中完成。同一时间只能跑一个采集，批次不允许合并。
/// </summary>
public sealed partial class BatchesViewModel : ObservableObject
{
    public BatchesViewModel()
    {
        _selectedBatch = Batches.FirstOrDefault();
        _selectedTabIndex = _selectedBatch is null ? 0 : DefaultTab(_selectedBatch);
        WatchBatch(null, _selectedBatch);

        // 弱事件：页面每次导航都会新建，服务和批次是长寿对象，不能被它们拽住。
        PropertyChangedEventManager.AddHandler(Collection, (_, _) => Refresh(), string.Empty);
    }

    private static CollectionService Collection => App.Collection;

    public ObservableCollection<CollectionBatch> Batches => BatchStore.Batches;

    [ObservableProperty]
    private bool _isBatchListOpen = true;

    [ObservableProperty]
    private CollectionBatch? _selectedBatch;

    [ObservableProperty]
    private int _selectedTabIndex;

    public bool IsBatchSelected => SelectedBatch is not null;
    public bool HasNoBatch => SelectedBatch is null;

    public CollectionBatch? Running => Batches.FirstOrDefault(b => b.State == BatchState.Collecting);
    public bool CanCreate => Running is null && Collection.IsPluginConnected;
    public string NewBatchTip => Running is { } b ? $"已有采集在进行：{b.Keyword}"
        : !Collection.IsPluginConnected ? "浏览器插件未连接"
        : "新建采集";

    public string PluginStatus => Collection.IsPluginConnected
        ? $"插件已连接{(Collection.PluginVersion is { } v ? $" · {v}" : "")}"
        : "插件未连接：请打开 Chrome 并启用 AM 采集插件";

    // 页签灰态：采集完成前后三个不可用；待处理清零后提交才可用；已中断只能看采集。
    public bool IsCollected => SelectedBatch?.State is BatchState.Collected or BatchState.Listing or BatchState.Completed;
    public bool CanSubmit => IsCollected && SelectedBatch!.BlockingCount == 0;

    public bool IsCollecting => SelectedBatch?.State == BatchState.Collecting;
    public bool IsReadOnly => SelectedBatch?.State.IsFinished() == true;
    public bool HasWarning => SelectedBatch?.Warning is not null;

    public string? ReadOnlyMessage => SelectedBatch?.State switch
    {
        BatchState.Interrupted => "此批次已中断，采集数据已丢弃，只读。",
        BatchState.Completed => "此批次已完成，只读。",
        _ => null,
    };

    public string CollectHeader => SelectedBatch is { } b ? $"采集 {b.ProgressText}" : "采集";
    public string PendingHeader => IsCollected && SelectedBatch!.BlockingCount > 0 ? $"待处理 {SelectedBatch.BlockingCount}" : "待处理";
    public string CopyHeader => IsCollected && SelectedBatch!.CopyFailedCount > 0 ? $"文案 {SelectedBatch.CopyFailedCount} 失败" : "文案";
    public string SubmitHeader => CanSubmit && SelectedBatch!.SkuCount > 0
        ? $"提交 {SelectedBatch.SucceededCount}/{SelectedBatch.SkuCount}"
        : "提交";

    public IReadOnlyList<CollectedProduct> Products => SelectedBatch switch
    {
        null => [],
        { State: BatchState.Interrupted } => [],
        { } b => BatchStore.ProductsFor(b),
    };

    /// <summary>
    /// 提交页：按商品折叠的定价结果，只含有保留 SKU 的商品。
    /// Refresh 很频繁，这里缓存起来，只在批次、状态或放弃数变化时重建，否则展开状态会被冲掉。
    /// </summary>
    public IReadOnlyList<PricedProductRow> PricedProducts
    {
        get
        {
            var key = (SelectedBatch, SelectedBatch?.State, AbandonedCount);
            if (_pricedProducts is null || !Equals(_pricedProductsKey, key))
            {
                _pricedProductsKey = key;
                _pricedProducts = BuildPricedProducts();
            }

            return _pricedProducts;
        }
    }

    private IReadOnlyList<PricedProductRow>? _pricedProducts;
    private object? _pricedProductsKey;

    private IReadOnlyList<PricedProductRow> BuildPricedProducts()
    {
        if (!IsCollected)
        {
            return [];
        }

        var currency = SelectedBatch!.CurrencyCode;
        return Products
            .Where(p => !p.IsAbandoned && p.Offer is not null && p.Prices is not null)
            .Select(p => PricedProductRow.Create(p.Offer!, p.Prices!, currency, CollapseOthers))
            .OfType<PricedProductRow>()
            .ToList();
    }

    /// <summary>同一时间只展开一个商品。</summary>
    private void CollapseOthers(PricedProductRow expanded)
    {
        foreach (var row in _pricedProducts ?? [])
        {
            if (row != expanded)
            {
                row.IsExpanded = false;
            }
        }
    }

    /// <summary>待处理页"价格异常"分段：每个有异常 SKU 的商品一行。</summary>
    public IReadOnlyList<PriceIssueRow> PriceIssues => IsCollected
        ? Products
            .Where(p => !p.IsAbandoned && p.Offer is not null && p.Prices?.Any(x => x.State == SkuPriceState.Abnormal) == true)
            .Select(p =>
            {
                var abnormal = p.Prices!.Where(x => x.State == SkuPriceState.Abnormal).ToList();
                return new PriceIssueRow(p, p.Offer!.Title, abnormal.Count, p.Prices!.Count, abnormal[0].Reason ?? "未知");
            })
            .ToList()
        : [];

    public bool HasPriceIssues => PriceIssues.Count > 0;

    // 待处理页"属性"分段。匹配结果变化（人工选择、撤销、放弃、重新匹配）时 _matchVersion 加一，
    // 分组才重建；否则 Refresh 很频繁，正在输入的筛选文字和选中项会被冲掉。
    private int _matchVersion;
    private IReadOnlyList<AttributeIssueGroup>? _attributeIssues;
    private object? _attributeIssuesKey;

    private BatchMatchingState? MatchingState => SelectedBatch is { } b && IsCollected ? BatchMatching.StateFor(b) : null;

    /// <summary>匹配做不了的原因（类型属性没拉到、规则文件有误）。</summary>
    public string? AttributeProblem => MatchingState?.Problem;

    public bool HasAttributeProblem => AttributeProblem is not null;

    public IReadOnlyList<AttributeIssueGroup> AttributeIssues
    {
        get
        {
            var key = (SelectedBatch, SelectedBatch?.State, _matchVersion);
            if (_attributeIssues is null || !Equals(_attributeIssuesKey, key))
            {
                _attributeIssuesKey = key;
                _attributeIssues = BuildAttributeIssues();
            }

            return _attributeIssues;
        }
    }

    public bool HasAttributeIssues => AttributeIssues.Count > 0;

    /// <summary>匹配做成了且没有未匹配的，显示"全部已匹配"。</summary>
    public bool AttributesAllMatched => MatchingState is { Problem: null } && !HasAttributeIssues;

    public IReadOnlyList<ManualEditRow> ManualEditRows => SelectedBatch is { } b && IsCollected
        ? b.ManualEdits.Select(e => new ManualEditRow(e,
            MatchingState?.Rules?.Attributes.FirstOrDefault(a => a.AttributeId == e.AttributeId)?.Name ?? e.AttributeId.ToString())).ToList()
        : [];

    public bool HasManualEdits => ManualEditRows.Count > 0;

    /// <summary>人工选择、放弃只在采集完成到上架完成之间可做；已完成只读。</summary>
    public bool CanEditAttributes => SelectedBatch?.State is BatchState.Collected or BatchState.Listing;

    private IReadOnlyList<AttributeIssueGroup> BuildAttributeIssues()
    {
        if (MatchingState is not { Problem: null, Schema: { } schema, Rules: { } rules } state)
        {
            return [];
        }

        var dictionaries = schema.Dictionaries.Where(d => d.Full).ToDictionary(d => d.AttributeId, d => d.Values);

        // (属性, 源值) → 含它的 SKU。只看未放弃商品里要提交的 SKU。
        var unmatched = Products
            .Where(p => !p.IsAbandoned && p.Offer is not null && p.AttributeMatches is not null)
            .SelectMany(p => p.AttributeMatches!.SelectMany((sku, i) => BatchMatching.UnmatchedBlocking(sku, state)
                .Select(m => (Product: p, Sku: p.Offer!.Skus[i], Match: m))))
            .ToList();

        return rules.Attributes
            .Where(a => state.BlockingAttributes.Contains(a.AttributeId))
            .Select(a =>
            {
                var options = Options(dictionaries.GetValueOrDefault(a.AttributeId));
                var rows = unmatched
                    .Where(x => x.Match.AttributeId == a.AttributeId)
                    .GroupBy(x => x.Match.SourceValue)
                    .OrderByDescending(g => g.Count())
                    .Select(g => new AttributeIssueRow(a.AttributeId, g.Key, g.First().Match.Failure ?? "",
                        g.Select(x => x.Product).Distinct().ToList(), g.Count(),
                        g.Select(x => x.Sku.ImageUrl ?? x.Product.Offer!.MainImages.FirstOrDefault())
                            .OfType<string>().Distinct().Take(3).ToList(),
                        options))
                    .ToList();
                return new AttributeIssueGroup(a.AttributeId, a.Name, rows);
            })
            .Where(g => g.Rows.Count > 0)
            .ToList();
    }

    private static IReadOnlyList<DictionaryOption> Options(IReadOnlyList<OzonDictionaryValue>? values)
    {
        if (values is null)
        {
            return [];
        }

        var duplicates = values.GroupBy(v => v.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return values
            .Select(v => new DictionaryOption(v.Id, v.Value, duplicates.Contains(v.Value) ? $"{v.Value}（{v.Id}）" : v.Value))
            .ToList();
    }

    /// <summary>已放弃的商品数。</summary>
    public int AbandonedCount => IsCollected ? Products.Count(p => p.IsAbandoned) : 0;

    public string AbandonedSummary => AbandonedCount > 0 ? $"已放弃 {AbandonedCount} 个商品" : "";

    public string PricingResultSummary => SelectedBatch is { } b && IsCollected
        ? $"售价币种 {b.CurrencyCode}。保留 {b.SkuCount} 个 SKU，价格丢弃 {b.DroppedSkuCount} 个（明细见任务记录）。"
        : "";

    partial void OnSelectedBatchChanged(CollectionBatch? oldValue, CollectionBatch? newValue)
    {
        WatchBatch(oldValue, newValue);
        Refresh();
        SelectedTabIndex = newValue is null ? 0 : DefaultTab(newValue);
    }

    [RelayCommand]
    private void ToggleBatchList() => IsBatchListOpen = !IsBatchListOpen;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task NewBatchAsync()
    {
        if (DialogService.ShowNewBatch() is not { } batch)
        {
            return;
        }

        Batches.Insert(0, batch);
        BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "开始采集", $"目标 {batch.TargetCount} 个商品"));
        SelectedBatch = batch;

        // 发送失败时服务已把批次标为中断。弹窗期间插件断开或已有采集，这里同样按中断处理。
        if (!await Collection.StartAsync(batch) && batch.State == BatchState.Collecting)
        {
            batch.State = BatchState.Interrupted;
            BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "中断采集，任务结束", "插件未连接，未开始"));
        }

        // 采集中就落库：应用中途退出，下次启动能看到它并按中断处理
        Collection.SaveBatch(batch);
        Refresh();

        // 采集的同时把该类型的属性和字典拉到本地，匹配阶段不联网（08 号问题 6）
        await WarmSchemaAsync(batch);
    }

    private async Task WarmSchemaAsync(CollectionBatch batch)
    {
        var settings = SettingsStore.LoadOzon();
        if (!settings.IsComplete)
        {
            return;
        }

        try
        {
            await TypeSchemaCache.GetAsync(settings, batch.DescriptionCategoryId, batch.TypeId);

            // 采集比拉取先结束时，匹配时还没有属性，这里补跑一次
            if (batch.State is BatchState.Collected && BatchMatching.StateFor(batch)?.Schema is null)
            {
                Rematch(batch);
            }
        }
        catch (OzonApiException error)
        {
            App.Snackbar.Show("类型属性拉取失败", $"{error.Message} 匹配前会再试。",
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(8));
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Running is not { } batch)
        {
            return;
        }

        var confirmed = await DialogService.ConfirmAsync(
            "停止采集",
            "停止将丢弃本次已采集的全部数据，任务结束。",
            "停止");
        if (!confirmed)
        {
            return;
        }

        await Collection.StopAsync("用户停止");
        Refresh();
    }

    /// <summary>开发版才有的功能（属性报告、以后的"写入规则"）。发布版构建为 Release，按钮不显示。</summary>
    public bool IsDevBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttributeReportCommand))]
    private bool _isReporting;

    private bool CanReport => IsCollected && !IsReporting;

    /// <summary>开发用：重新拉该类型的属性和字典，对照本批次 1688 数据生成覆盖报告。</summary>
    [RelayCommand(CanExecute = nameof(CanReport))]
    private async Task AttributeReportAsync()
    {
        if (SelectedBatch is not { } batch)
        {
            return;
        }

        var settings = SettingsStore.LoadOzon();
        if (!settings.IsComplete)
        {
            App.Snackbar.Show("无法生成报告", "请先在设置里填写 Ozon 凭据。",
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(8));
            return;
        }

        IsReporting = true;
        try
        {
            // 报告用来定规则，要拿最新的属性和字典
            var schema = await TypeSchemaCache.RefreshAsync(settings, batch.DescriptionCategoryId, batch.TypeId);
            var products = Products;
            var path = await Task.Run(() =>
            {
                // 规则每次现读，改完规则文件直接点报告就能看到效果
                RuleSet? rules = null;
                string? rulesError = null;
                try
                {
                    rules = RuleStore.Load();
                }
                catch (RuleFileException error)
                {
                    rulesError = error.Message;
                }

                return AttributeReport.Write(batch, products, schema, rules, rulesError);
            });
            App.Snackbar.Show("属性报告已生成", path,
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(10));
        }
        catch (OzonApiException error)
        {
            App.Snackbar.Show("拉取属性失败", error.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsReporting = false;
        }
    }

    [RelayCommand]
    private void AbandonProduct(CollectedProduct product) => Abandon([product]);

    /// <summary>"属性"分段：放弃含该源值的全部商品。放弃是终态，不弹确认（19 号）。</summary>
    [RelayCommand]
    private void AbandonAttributeProducts(AttributeIssueRow row) => Abandon(row.Products);

    private void Abandon(IReadOnlyList<CollectedProduct> products)
    {
        if (SelectedBatch is not { } batch || !CanEditAttributes)
        {
            return;
        }

        foreach (var product in products.Where(p => !p.IsAbandoned))
        {
            product.IsAbandoned = true;
            BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "放弃商品",
                product.Offer?.Title ?? $"商品 {product.Index}"));
        }

        BatchMatching.RecountBlocking(batch);
        Collection.SaveBatch(batch);
        _matchVersion++;
        Refresh();
    }

    /// <summary>为一个源值选定字典值，本批次含该源值的 SKU 一起生效，不写规则（19 号）。</summary>
    [RelayCommand]
    private void ApplyAttributeValue(AttributeIssueRow row)
    {
        if (SelectedBatch is not { } batch || !CanEditAttributes || row.Selected is not { } option)
        {
            return;
        }

        batch.ManualEdits.RemoveAll(e => e.AttributeId == row.AttributeId && e.Source == row.EditSource);
        batch.ManualEdits.Add(new ManualEdit(row.AttributeId, row.EditSource, option.Id, option.Value));
        BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "人工选择属性",
            $"属性 {row.AttributeId}：{row.SourceText} → {option.Value}（{option.Id}），{row.CountText}"));
        Rematch(batch);
    }

    [RelayCommand]
    private void UndoManualEdit(ManualEditRow row)
    {
        if (SelectedBatch is not { } batch || !CanEditAttributes)
        {
            return;
        }

        batch.ManualEdits.Remove(row.Edit);
        BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "撤销人工选择", row.Text));
        Rematch(batch);
    }

    /// <summary>改了规则文件后按新规则重跑（开发版常用），或拉取属性后重跑。</summary>
    [RelayCommand]
    private void RematchAttributes()
    {
        if (SelectedBatch is { } batch && CanEditAttributes)
        {
            Rematch(batch);
        }
    }

    /// <summary>类型属性没拉到时，从待处理页手动拉取。</summary>
    [RelayCommand]
    private async Task FetchSchemaAsync()
    {
        if (SelectedBatch is not { } batch)
        {
            return;
        }

        var settings = SettingsStore.LoadOzon();
        if (!settings.IsComplete)
        {
            App.Snackbar.Show("无法拉取", "请先在设置里填写 Ozon 凭据。",
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(8));
            return;
        }

        try
        {
            await TypeSchemaCache.RefreshAsync(settings, batch.DescriptionCategoryId, batch.TypeId);
            Rematch(batch);
        }
        catch (OzonApiException error)
        {
            App.Snackbar.Show("拉取属性失败", error.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(8));
        }
    }

    private void Rematch(CollectionBatch batch)
    {
        BatchMatching.Apply(batch);
        Collection.SaveBatch(batch);
        _matchVersion++;
        Refresh();
    }

    /// <summary>选中批次时跳到最可能要处理的页签。</summary>
    private static int DefaultTab(CollectionBatch batch) => batch.State switch
    {
        BatchState.Collected or BatchState.Listing when batch.BlockingCount > 0 => 1,
        BatchState.Collected or BatchState.Listing => 3,
        BatchState.Completed => 3,
        _ => 0,
    };

    /// <summary>选中批次的进度和状态变化要刷新页签标题等派生属性。</summary>
    private void WatchBatch(CollectionBatch? oldValue, CollectionBatch? newValue)
    {
        if (oldValue is not null)
        {
            PropertyChangedEventManager.RemoveHandler(oldValue, OnBatchChanged, string.Empty);
        }

        if (newValue is not null)
        {
            PropertyChangedEventManager.AddHandler(newValue, OnBatchChanged, string.Empty);
        }
    }

    private void OnBatchChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        NewBatchCommand.NotifyCanExecuteChanged();
        AttributeReportCommand.NotifyCanExecuteChanged();
    }
}
