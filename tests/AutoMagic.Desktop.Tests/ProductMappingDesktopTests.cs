using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutoMagic.Application.ExchangeRates;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Application.Search;
using AutoMagic.Contracts.Protocol;
using AutoMagic.Desktop;
using AutoMagic.Desktop.ViewModels;
using AutoMagic.Infrastructure.Collection;

namespace AutoMagic.Desktop.Tests;

public sealed class ProductMappingDesktopTests
{
    [Fact]
    public Task Success_ShowsPerSkuSuggestionsAndWindowBindsToNewResults() => OnDispatcher(async () =>
    {
        var mapper = new ControlledMapper { CompleteImmediately = true };
        var vm = await CreateViewModel(mapper);
        Assert.True(vm.RunQwenMappingCommand.CanExecute(null));
        await vm.RunQwenMappingCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.ProductMappingRows.Count); // Two actual SKUs inherit one shared attribute.
        Assert.DoesNotContain(vm.ProductMappingIssues, i => i.Severity == "error");
        Assert.Contains("建议已生成", vm.FieldMatchingFinalStatus);
        Assert.Contains("\"readyForListing\": false", vm.FieldMatchingFinalOutputJson);
        Assert.Contains("第一阶段尚未提供价格", vm.FieldMatchingFinalOutputJson);
        Assert.DoesNotContain("\\u7b2c", vm.FieldMatchingFinalOutputJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourcePath", vm.FieldMatchingInputJson);
        Assert.DoesNotContain("collectionId", vm.QwenRequestJson);
        Assert.Contains("材质：涤纶", vm.QwenRequestJson);
        Assert.Equal(2, vm.ProductSkuIdentityRows.Count);
        Assert.Equal(2, vm.ProductSkuIdentityRows.Select(row => row.MerchantSku).Distinct().Count());
        Assert.Contains("稳定商家 SKU", vm.ProductSkuPlanStatus);
        var window = new MainWindow(vm); // Instantiate WPF/XAML, but do not show or start the real app.
        window.Measure(new Size(1280, 900));
        window.Arrange(new Rect(0, 0, 1280, 900));
        var tabs = Descendants(window).OfType<TabItem>().ToArray();
        var mappingTab = tabs.Single(t => Equals(t.Header, "字段匹配"));
        mappingTab.IsSelected = true;
        window.UpdateLayout();
        var resultTab = Descendants(mappingTab).OfType<TabItem>().Single(t => Equals(t.Header, "AI 映射建议"));
        resultTab.IsSelected = true;
        window.UpdateLayout();
        var grid = (DataGrid)resultTab.Content;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        grid.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
        Assert.Same(vm.ProductMappingRows, grid.ItemsSource);
        Assert.True(grid.IsReadOnly);
        var skuTab = Descendants(mappingTab).OfType<TabItem>().Single(t => Equals(t.Header, "SKU 身份计划"));
        skuTab.IsSelected = true;
        window.UpdateLayout();
        var skuGrid = Descendants(skuTab).OfType<DataGrid>().Single();
        Assert.Same(vm.ProductSkuIdentityRows, skuGrid.ItemsSource);
        Assert.True(skuGrid.IsReadOnly);
        window.Close();
    });

    [Fact]
    public Task FailedDetails_AreNotOfferedForFieldMatching() => OnDispatcher(async () =>
    {
        var vm = await CreateViewModel(new ControlledMapper { CompleteImmediately = true });
        var successful = Detail("111111");
        var partial = Detail("222222") with { Status = "partial" };
        var failed = Detail("333333") with
        {
            Status = "failed",
            Facts = [],
            FailureCode = "network_error",
            Errors = ["测试失败"],
        };
        var payload = new SearchResultPayload("测试", "2026-09-21T00:00:00Z", 3, [], null,
            DetailResults: [failed, partial, successful]);
        typeof(MainViewModel).GetMethod("ApplyCollectionResult", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [payload, "test-collection"]);

        Assert.Equal(2, vm.FieldMatchingProducts.Count);
        Assert.DoesNotContain(vm.FieldMatchingProducts, item => item.Status == "failed");
        Assert.Same(successful, vm.SelectedFieldMatchingDetail);
    });

    [Theory]
    [InlineData("product")]
    [InlineData("category")]
    [InlineData("cancel")]
    [InlineData("credentials")]
    public Task LateResponse_AfterContextChangeCannotOverwriteCurrentPreview(string change) => OnDispatcher(async () =>
    {
        var mapper = new ControlledMapper();
        var vm = await CreateViewModel(mapper);
        var run = vm.RunQwenMappingCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunningQwenMapping);
        Assert.NotNull(mapper.Request);
        switch (change)
        {
            case "product": vm.SelectedFieldMatchingDetail = Detail("222222"); break;
            case "category": vm.SelectedOzonType = new(999, "另一类型", "另一类型"); break;
            case "cancel": vm.CancelProductMappingCommand.Execute(null); break;
            default: vm.OzonApiKey = "new-test-key"; break;
        }
        // Deliberately simulate a provider ignoring cancellation and returning an old response.
        mapper.Completion.SetResult(mapper.Result(mapper.Request!));
        await run;
        Assert.Empty(vm.ProductMappingRows);
        Assert.DoesNotContain("provider-old", vm.FieldMatchingFinalOutputJson);
        Assert.False(vm.IsRunningQwenMapping);
        if (change == "category") Assert.False(vm.RunQwenMappingCommand.CanExecute(null));
    });

    [Fact]
    public Task ChangingProductClearsPreviousDictionarySummaryAndRows() => OnDispatcher(async () =>
    {
        var vm = await CreateViewModel(new ControlledMapper { CompleteImmediately = true });
        await vm.RunQwenMappingCommand.ExecuteAsync(null);
        Assert.Contains("收到 AI 响应 1 轮", vm.QwenDictionaryStatus);
        vm.SelectedFieldMatchingDetail = Detail("222222");
        Assert.Empty(vm.ProductMappingRows);
        Assert.Equal("尚未查询本次商品的字典候选。", vm.QwenDictionaryStatus);
    });

    [Fact]
    public Task InvalidResponse_IsClearlyMarkedFailedAndNeverDisplayedAsValidated() => OnDispatcher(async () =>
    {
        var mapper = new ControlledMapper { CompleteImmediately = true, InvalidEvidence = true };
        var vm = await CreateViewModel(mapper);
        await vm.RunQwenMappingCommand.ExecuteAsync(null);
        Assert.Contains("校验错误", vm.FieldMatchingFinalStatus);
        Assert.Contains(vm.ProductMappingIssues, i => i.Code == "evidence.fabricated");
        Assert.All(vm.ProductMappingRows, r => Assert.Equal("校验失败", r.CheckDisplay));
    });

    private static async Task<MainViewModel> CreateViewModel(ControlledMapper mapper)
    {
        var attribute = new OzonAttributeDefinition(10, 0, "材质", "", "String", false, true, 0, 1, "");
        var schemaService = Stub<IOzonSchemaService>((name, _) => name == "GetCategorySchemaAsync"
            ? Task.FromResult(new OzonCategorySchema(100, 200, DateTimeOffset.UtcNow, [attribute])) : null);
        var vm = new MainViewModel(Stub<ISearchBridge>(), Stub<IExchangeRateService>(), schemaService,
            Stub<ILocalOzonCategoryCatalog>(), Stub<IOzonTestSettingsStore>(), Stub<IQwenTestSettingsStore>(),
            new CollectionSnapshotStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"am-no-write-{Guid.NewGuid():N}")),
            new ProductMappingRunner(mapper, Stub<IOzonDictionaryService>()),
            new CategoryRuleMatchingEngine(CategoryRuleCatalog.Empty));
        vm.OzonClientId = "test-client";
        vm.OzonApiKey = "test-ozon-key";
        vm.QwenApiKey = "test-qwen-key";
        var type = new OzonTypeOption(200, "测试类型", "测试类型");
        vm.SelectedOzonCategory = new(100, "测试类目", "测试类目", [type]);
        vm.SelectedOzonType = type;
        vm.SelectedFieldMatchingDetail = Detail("111111");
        await vm.LoadOzonSchemaCommand.ExecuteAsync(null);
        return vm;
    }

    private static DetailCollectionResultDto Detail(string offer) => new(0, 1, "测试商品",
        $"https://detail.1688.com/offer/{offer}.html", "success",
        $"https://detail.1688.com/offer/{offer}.html", "2026-09-20T00:00:00Z", "测试商品",
        [new("材质", "涤纶", "detail")], [], [], Raw: JsonSerializer.SerializeToElement(new
        {
            skuMatrixStatus = "verified",
            skuCombinations = new[]
            {
                new { combinationKey = "red-S", verification = "structured-json", skuId = "sku-red", options = new { 颜色 = "红色", 尺码 = "S" } },
                new { combinationKey = "blue-M", verification = "structured-json", skuId = "sku-blue", options = new { 颜色 = "蓝色", 尺码 = "M" } },
            },
        }));

    private sealed class ControlledMapper : IProductSemanticMapper
    {
        public bool CompleteImmediately { get; init; }
        public bool InvalidEvidence { get; init; }
        public ProductMappingRequest? Request { get; private set; }
        public TaskCompletionSource<ProductMappingCallResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProductMappingCallResult> MapAsync(QwenApiCredentials credentials, ProductMappingRequest request, CancellationToken token)
        {
            Request = request;
            return CompleteImmediately ? Task.FromResult(Result(request)) : Completion.Task;
        }
        public ProductMappingCallResult Result(ProductMappingRequest request) => new("provider-old", "test", "test", "{}",
            new(1, 1, 2), new(request.RequestId,
                [new(10, null, "suggested", [new("полиэстер", null)],
                    [InvalidEvidence ? "invented" : request.Facts.Single(f => f.Label == "材质").FactId], "材质明确。")],
                request.Skus.Select(s => new ProductVariantSuggestion(s.VariantKey, [])).ToArray(), []), []);
    }

    private static Task OnDispatcher(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Stub<T>(Func<string, object?[]?, object?>? answer = null) where T : class
    {
        var stub = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)stub).Answer = answer;
        return stub;
    }

    public class StubProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?>? Answer { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var custom = Answer?.Invoke(method!.Name, args);
            if (custom is not null) return custom;
            var type = method!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType)
                    .Invoke(null, [resultType.IsValueType ? Activator.CreateInstance(resultType) : null]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
