using System.IO;
using System.Windows;
using AM.Bridge;
using AM.Core.Pricing;
using AM.Desktop.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AM.Desktop.Services;

/// <summary>
/// 采集服务：通过 BridgeServer 和插件通信，管理当前唯一的采集批次。
/// 商品逐条写入内存暂存；两遍结束才算采集完成；停止、断连、关闭即中断，暂存全部丢弃。
/// </summary>
public sealed partial class CollectionService : ObservableObject
{
    // 抓取间隔和健康检查阈值应来自设置，设置页还没有这两项。数值是占位，没有依据。
    private const int IntervalMs = 1500;
    private const double HealthFailureRate = 0.3;

    private readonly BridgeServer _bridge = new();

    public CollectionService()
    {
        _bridge.Connected += () => OnUi(() => IsPluginConnected = true);
        _bridge.Disconnected += () => OnUi(OnDisconnected);
        _bridge.MessageReceived += envelope => OnUi(() => Handle(envelope));
    }

    [ObservableProperty]
    private bool _isPluginConnected;

    [ObservableProperty]
    private string? _pluginVersion;

    /// <summary>正在采集的批次。同一时间只有一个。</summary>
    [ObservableProperty]
    private CollectionBatch? _active;

    public void Start() => _bridge.Start();

    public async Task<bool> StartAsync(CollectionBatch batch)
    {
        if (Active is not null || !IsPluginConnected)
        {
            return false;
        }

        Active = batch;

        // 搜索页粗筛：上限由公式反推；下限是选填的采购价下限，未设时不限
        var sent = await _bridge.SendAsync(BridgeEnvelope.Create(BridgeProtocol.Types.CollectStart,
            new CollectStartPayload(batch.Id, batch.Keyword, batch.TargetCount, ToProtocolSort(batch.Sort),
                PurchasePriceMin: PricingEngine.SearchPriceFloor(batch.Pricing),
                PurchasePriceMax: PricingEngine.SearchPriceCeiling(batch.Pricing),
                IntervalMs, HealthFailureRate)));
        if (!sent)
        {
            Interrupt(batch, "插件未响应");
        }

        return sent;
    }

    public async Task StopAsync(string reason)
    {
        if (Active is not { } batch)
        {
            return;
        }

        Interrupt(batch, reason);
        await _bridge.SendAsync(BridgeEnvelope.Create(BridgeProtocol.Types.CollectStop, new CollectStopPayload(batch.Id)));
    }

    private void Handle(BridgeEnvelope envelope)
    {
        if (envelope.Type == BridgeProtocol.Types.ExtensionHello)
        {
            PluginVersion = envelope.PayloadAs<ExtensionHelloPayload>()?.ExtensionVersion;
            return;
        }

        if (envelope.Type == BridgeProtocol.Types.ProbeResult)
        {
            SaveProbe(envelope);
            return;
        }

        // 只处理当前批次的消息；停止后插件可能还会发来几条，直接忽略。
        var batchId = envelope.Payload?["batchId"]?.GetValue<string>();
        if (Active is not { } batch || batchId != batch.Id)
        {
            return;
        }

        switch (envelope.Type)
        {
            case BridgeProtocol.Types.CollectProgress when envelope.PayloadAs<CollectProgressPayload>() is { } p:
                batch.CollectPass = p.Pass;
                break;

            case BridgeProtocol.Types.CollectItem when envelope.PayloadAs<CollectItemPayload>() is { } item:
                ApplyItem(batch, item);
                break;

            case BridgeProtocol.Types.CollectPassDone when envelope.PayloadAs<CollectPassDonePayload>() is { } done:
                BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, $"第 {done.Pass} 遍完成",
                    $"成功 {done.Total - done.Failed}，失败 {done.Failed}"));
                if (done.SkipSecondPass)
                {
                    batch.Warning = "采集可能失效：第 1 遍失败率超过阈值，已跳过第 2 遍。";
                }
                break;

            case BridgeProtocol.Types.CollectFinished when envelope.PayloadAs<CollectFinishedPayload>() is { } f:
                batch.CollectedCount = f.Succeeded;
                BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "采集完成",
                    $"成功 {f.Succeeded}，失败 {f.Failed}"));

                // 定价和属性匹配在切换状态之前做，页签刷新时价格和阻断数已就绪。匹配只看要提交的 SKU，所以排在定价后面。
                BatchPricing.Apply(batch);
                LogMatching(batch, BatchMatching.Apply(batch));
                batch.State = BatchState.Collected;
                DumpBatch(batch);
                SaveBatch(batch);
                Active = null;
                break;

            case BridgeProtocol.Types.CollectFailed:
                Interrupt(batch, $"插件报错：{envelope.Error?.Message ?? "未知错误"}");
                break;

            case BridgeProtocol.Types.CollectStopped:
                break;
        }
    }

    private static void ApplyItem(CollectionBatch batch, CollectItemPayload item)
    {
        var products = BatchStore.ProductsFor(batch);
        var status = item.Success ? "完成" : item.Pass == 1 ? "待重抓" : $"最终失败：{item.FailureCode}";
        var title = item.Offer?.Title ?? (item.OfferId is { } id ? $"商品 {id}" : $"商品 {item.Index}");
        var skuCount = item.Offer?.Skus.Count ?? 0;
        var row = products.FirstOrDefault(p => p.Index == item.Index);
        if (row is null)
        {
            products.Add(new CollectedProduct(item.Index, title, skuCount, status) { Offer = item.Offer });
        }
        else
        {
            // 第二遍失败不覆盖第一遍可能已有的标题
            row.Title = item.Offer?.Title ?? row.Title;
            row.SkuCount = item.Offer is null ? row.SkuCount : skuCount;
            row.Status = status;
            row.Offer = item.Offer ?? row.Offer;
        }

        batch.CollectedCount = products.Count(p => p.Status == "完成");
    }

    /// <summary>
    /// 页面探测结果落到 %LocalAppData%\AM\probes\，一次一个文件，原样保存 JSON。
    /// 仅开发阶段使用，接入真实采集后连同插件里的 probe.js、popup 一起删除。
    /// </summary>
    private void SaveProbe(BridgeEnvelope envelope)
    {
        if (envelope.Payload is not { } payload)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(SettingsStore.DataDirectory, "probes");
            Directory.CreateDirectory(directory);
            var kind = payload["pageKind"]?.GetValue<string>() ?? "unknown";
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.json";
            File.WriteAllText(Path.Combine(directory, name), payload.ToJsonString(new() { WriteIndented = true }));
            FileSaved?.Invoke("页面探测结果已保存", Path.Combine(directory, name));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FileSaveFailed?.Invoke("页面探测结果保存失败", error.Message);
        }
    }

    /// <summary>开发阶段落盘的文件（探测结果、批次数据）写好或写失败时触发，参数是标题和路径或原因。</summary>
    public event Action<string, string>? FileSaved;
    public event Action<string, string>? FileSaveFailed;

    /// <summary>
    /// 采集完成的批次原样写到 %LocalAppData%\AM\batches\，用来核对价格、库存、图片的真实命中率（M4）。
    /// 仅开发阶段使用，接入 SQLite（M2）后删除。中断的批次按规则丢弃，不写。
    /// </summary>
    private void DumpBatch(CollectionBatch batch)
    {
        try
        {
            var directory = Path.Combine(SettingsStore.DataDirectory, "batches");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{batch.Id}.json");
            var dump = new
            {
                batch.Id,
                batch.Keyword,
                batch.CategoryPath,
                batch.TypeId,
                batch.CurrencyCode,
                batch.Pricing,
                SearchPriceFloor = PricingEngine.SearchPriceFloor(batch.Pricing),
                SearchPriceCeiling = PricingEngine.SearchPriceCeiling(batch.Pricing),
                Products = BatchStore.ProductsFor(batch).Select(p => new { p.Index, p.Status, p.Offer, p.Prices }),
            };
            var options = new System.Text.Json.JsonSerializerOptions(BridgeJson.Options)
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dump, options));
            FileSaved?.Invoke("批次数据已保存", path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            FileSaveFailed?.Invoke("批次数据保存失败", error.Message);
        }
    }

    /// <summary>
    /// 保存批次到数据库。
    /// </summary>
    /// <summary>
    /// 批次连同商品写库。批次状态或商品变化后调用。
    /// 须在 UI 线程上发起：序列化要读界面集合；写库是异步的，不阻塞界面。
    /// </summary>
    public async void SaveBatch(CollectionBatch batch)
    {
        try
        {
            await App.BatchRepo.SaveAsync(batch);
        }
        catch (Exception error)
        {
            FileSaveFailed?.Invoke("批次数据库保存失败", error.Message);
        }
    }

    private void OnDisconnected()
    {
        IsPluginConnected = false;
        PluginVersion = null;
        if (Active is { } batch)
        {
            Interrupt(batch, "插件断开");
        }
    }

    private static void LogMatching(CollectionBatch batch, int unmatchedProducts)
    {
        var detail = BatchMatching.StateFor(batch)?.Problem
            ?? (unmatchedProducts > 0 ? $"{unmatchedProducts} 个商品有属性未匹配，见待处理页" : "全部属性已匹配");
        BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "属性匹配", detail));
    }

    /// <summary>中断是终态：批次标为已中断，暂存数据全部丢弃。</summary>
    private void Interrupt(CollectionBatch batch, string reason)
    {
        var products = BatchStore.ProductsFor(batch);
        var collected = products.Count(p => p.Status == "完成");
        products.Clear();
        batch.State = BatchState.Interrupted;
        BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "中断采集，任务结束",
            $"{reason}，已采集 {collected} 个，全部丢弃"));
        SaveBatch(batch);
        if (ReferenceEquals(Active, batch))
        {
            Active = null;
        }
    }

    private static string ToProtocolSort(string sort) => sort switch
    {
        "销量" => CollectSort.Sales,
        _ => CollectSort.Default,
    };

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    /// <summary>应用退出时关闭管道。进行中的采集随插件断连按中断处理，暂存数据丢弃。</summary>
    public void Shutdown() => _bridge.Shutdown(TimeSpan.FromSeconds(2));
}
