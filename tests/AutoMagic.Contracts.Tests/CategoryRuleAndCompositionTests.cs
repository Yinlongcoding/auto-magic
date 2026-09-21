using AutoMagic.Application.Ozon.Mapping;

namespace AutoMagic.Contracts.Tests;

public sealed class CategoryRuleAndCompositionTests
{
    [Fact]
    public void EmptyCatalog_IsValidLearningModeAndPreservesEveryVariantSlot()
    {
        var request = Request();
        var result = new CategoryRuleMatchingEngine(CategoryRuleCatalog.Empty).Match(request);

        Assert.False(result.HasDeterministicSuggestions);
        Assert.Empty(result.Suggestions.ProductMappings);
        Assert.Equal(request.Skus.Select(sku => sku.VariantKey),
            result.Suggestions.Variants.Select(variant => variant.VariantKey));
        Assert.Empty(result.Gaps);
    }

    [Fact]
    public void TypeOverride_ReplacesOnlySameCategoryBindingAndCommonModuleRemainsReusable()
    {
        var catalog = new CategoryRuleCatalog("test-1",
            [new("common.color", "color", ["颜色", "颜色分类"])],
            [
                new("category.keyboard", 100, RuleProfileLayer.CategoryCommon, new HashSet<long>(),
                    [new("keyboard.color", "color", 20, RuleValueScope.Variant, RuleValueStrategy.DictionaryLookup)]),
                new("type.keyboard.special", 100, RuleProfileLayer.TypeOverride, new HashSet<long> { 200 },
                    [new("keyboard.color.direct", "color", 20, RuleValueScope.Variant, RuleValueStrategy.DirectText)]),
            ]);

        var result = new CategoryRuleMatchingEngine(catalog).Match(Request());

        Assert.Equal(2, result.Applications.Count);
        Assert.All(result.Applications, application =>
        {
            Assert.Equal(RuleProfileLayer.TypeOverride, application.Layer);
            Assert.Equal("keyboard.color.direct", application.BindingId);
        });
        Assert.All(result.Suggestions.Variants,
            variant => Assert.Equal(ProductMappingStatuses.Suggested, Assert.Single(variant.Mappings).Status));
    }

    [Fact]
    public void CatalogValidation_MakesCommonVersusPatchAnExplicitBoundary()
    {
        var invalidCommon = new CategoryRuleCatalog("test", [],
            [new("bad", 100, RuleProfileLayer.CategoryCommon, new HashSet<long> { 200 }, [])]);
        var invalidPatch = new CategoryRuleCatalog("test", [],
            [new("bad", 100, RuleProfileLayer.TypeOverride, new HashSet<long>(), [])]);

        Assert.Throws<ArgumentException>(() => new CategoryRuleMatchingEngine(invalidCommon));
        Assert.Throws<ArgumentException>(() => new CategoryRuleMatchingEngine(invalidPatch));
    }

    [Fact]
    public void Composer_CreatesOneOzonItemPerRealSkuAndInheritsProductAttributes()
    {
        var request = Request();
        var response = new ProductMappingResponse(request.RequestId,
            [new(10, null, ProductMappingStatuses.Suggested,
                [new("塑料", null)], ["product:material"], "源材质。")],
            request.Skus.Select(sku => new ProductVariantSuggestion(sku.VariantKey,
                [new(20, null, ProductMappingStatuses.Suggested,
                    [new(sku.Options["颜色"]!, null)], [sku.FactIds[0]], "源颜色。")])).ToArray(), []);
        var validation = ProductMappingValidator.Validate(request, response);

        var result = OzonFieldCompositionEngine.Compose(request, response, validation);

        Assert.True(validation.ContractValid);
        Assert.Equal(2, result.Request.Items.Count);
        Assert.All(result.Request.Items, item =>
        {
            Assert.Equal(2, item.Attributes.Count);
            Assert.Contains(item.Attributes, attribute => attribute.Id == 10 && attribute.Values[0].Value == "塑料");
            Assert.StartsWith("AM-", item.OfferId, StringComparison.Ordinal);
        });
        Assert.Contains(result.Issues, issue => issue.Code == "composition.commercial_fields_deferred");
        Assert.False(result.ReadyForSubmit);
    }

    [Fact]
    public async Task Runner_AppliesDeterministicCategoryRuleBeforeFinalValidation()
    {
        var input = ProductMappingInputBuilder.Create(ProductMappingTests.Fixture.Input(), "rules-first");
        var catalog = new CategoryRuleCatalog("test",
            [new("common.material", "material", ["材质"])],
            [new("category.test", 100, RuleProfileLayer.CategoryCommon, new HashSet<long>(),
                [new("test.material", "material", 10, RuleValueScope.Product, RuleValueStrategy.DirectText)])]);
        var mapper = new ProductMappingTests.Fixture.Mapper(request => ProductMappingTests.Fixture.Response(request));
        var runner = new ProductMappingRunner(mapper, new ProductMappingTests.Fixture.Dictionary(),
            new CategoryRuleMatchingEngine(catalog));

        var run = await runner.RunAsync(input, new("test"), new("client", "key"), null, CancellationToken.None);

        var material = Assert.Single(run.Response!.ProductMappings, mapping => mapping.AttributeId == 10);
        Assert.Equal("涤纶", Assert.Single(material.Values).Text);
        Assert.Contains("test.material", material.Reason);
    }

    private static ProductMappingRequest Request()
    {
        var skus = new[]
        {
            new ProductMappingSku("variant:red", "AM-RED", "source-sku-id", "red", "source-red",
                new Dictionary<string, string?> { ["颜色"] = "红色" }, ["sku:red:color"]),
            new ProductMappingSku("variant:white", "AM-WHITE", "source-sku-id", "white", "source-white",
                new Dictionary<string, string?> { ["颜色"] = "白色" }, ["sku:white:color"]),
        };
        return new("2.1", "request", "collection", "source", "1688:source", "2026-09-21T00:00:00Z",
            "fingerprint", 100, 200, "电子产品 > 键盘",
            [
                new("product:title", "product", "商品标题", "机械键盘", "collected-title", "$.title"),
                new("product:material", "product", "材质", "塑料", "detail", "$.facts[0]"),
                new("sku:red:color", "variant:red", "颜色", "红色", "verified-sku-option", "$.sku[0]"),
                new("sku:white:color", "variant:white", "颜色分类", "白色", "verified-sku-option", "$.sku[1]"),
            ],
            skus,
            [
                new(10, 0, "材质", "", "String", false, true, 1, 0, []),
                new(20, 0, "颜色", "", "String", false, true, 1, 0, []),
            ]);
    }
}
