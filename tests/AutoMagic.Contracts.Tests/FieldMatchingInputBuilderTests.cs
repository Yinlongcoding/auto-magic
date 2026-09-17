using System.Text.Json;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Contracts.Tests;

public sealed class FieldMatchingInputBuilderTests
{
    [Fact]
    public void Create_PreservesFactsAndAddsRawEvidenceWithoutDeduplication()
    {
        var raw = JsonSerializer.SerializeToElement(new
        {
            imageUrls = new[] { "https://example.test/1.jpg" },
            priceTexts = new[] { "¥35.00", "¥35.00" },
            skuTexts = new[] { "规格A ¥35 库存8套" },
        });
        var detail = new DetailCollectionResultDto(
            1,
            2,
            "键帽",
            "https://detail.m.1688.com/page/index.html?offerId=1081315169880",
            "success",
            "https://detail.1688.com/offer/1081315169880.html",
            "2026-09-15T06:51:24.374Z",
            "键帽 - 阿里巴巴",
            [
                new DetailFactDto("商品名称", "键帽", "document.title"),
                new DetailFactDto("材质", "PC、PBT", "decision-attributes"),
                new DetailFactDto("材质", "PC,PBT", "cpv-attributes"),
            ],
            [],
            [],
            Raw: raw);
        var schema = new OzonCategorySchema(
            10,
            20,
            DateTimeOffset.UtcNow,
            [new OzonAttributeDefinition(30, 0, "Материал", "说明", "String", true, true, 0, 5, "Основные")]);

        var input = FieldMatchingInputBuilder.Create(
            "COL-1",
            "MAP-1",
            detail,
            schema,
            "配件 > 键帽",
            true);

        Assert.Equal("1081315169880", input.ProductRef.OfferId);
        Assert.Equal(3, input.Source.Facts.Count);
        Assert.Equal("f001", input.Source.Facts[0].FactId);
        Assert.Equal("title", input.Source.Facts[0].Kind);
        Assert.Equal("$.facts[2]", input.Source.Facts[2].SourcePath);
        Assert.Equal(2, input.Source.PriceEvidence.Count);
        Assert.Single(input.Source.SkuEvidence);
        Assert.Single(input.Source.Media);
        Assert.Single(input.Target.Attributes);
        Assert.True(input.Target.CategoryAndTypeConfirmedByUser);
        Assert.False(input.Rules.AllowSyntheticSku);
        Assert.Equal("China", input.Rules.DefaultOriginCountry);
        Assert.Equal("sourceOfferId", input.Rules.CardGroupingStrategy);
    }

    [Fact]
    public void Create_WorksBeforeOzonSchemaIsAvailable()
    {
        var detail = new DetailCollectionResultDto(
            0,
            1,
            "商品",
            "https://detail.m.1688.com/page/index.html?offerId=123456",
            "partial",
            null,
            null,
            null,
            [],
            [],
            []);

        var input = FieldMatchingInputBuilder.Create("COL-1", "MAP-1", detail);

        Assert.Equal("123456", input.ProductRef.OfferId);
        Assert.Empty(input.Target.Attributes);
        Assert.Null(input.Target.DescriptionCategoryId);
        Assert.False(input.Target.CategoryAndTypeConfirmedByUser);
    }

    [Fact]
    public void Create_AddsAuditableGenderAndOriginDerivedFacts()
    {
        var detail = new DetailCollectionResultDto(
            0, 1, "女士连衣裙", "https://detail.1688.com/offer/123456.html", "success",
            null, null, "女士连衣裙",
            [
                new DetailFactDto("商品名称", "女士连衣裙", "document.title"),
                new DetailFactDto("产地", "浙江", "dom-pair"),
            ], [], []);

        var input = FieldMatchingInputBuilder.Create("COL-1", "MAP-1", detail);

        var gender = Assert.Single(input.Source.Facts, fact => fact.Source == "inference:title");
        Assert.Equal("女", gender.Value);
        Assert.Equal(["f001"], gender.DerivedFromFactIds);
        var origin = Assert.Single(input.Source.Facts, fact => fact.Source == "conversion:origin-country");
        Assert.Equal("China", origin.Value);
        Assert.Equal(["f002"], origin.DerivedFromFactIds);
    }

    [Fact]
    public void Create_Normalizes1688ColorOptionsAndKeepsNamelessOptionsUnresolved()
    {
        var detail = new DetailCollectionResultDto(
            0, 1, "礼服裙", "https://detail.1688.com/offer/123456.html", "success",
            null, null, "礼服裙",
            [new DetailFactDto("颜色", "1绿色有现货、2黑色有现货、6有现货、10酒红有现货", "normal-attributes")],
            [], []);

        var input = FieldMatchingInputBuilder.Create("COL-1", "MAP-1", detail);

        Assert.Equal(["绿色", "黑色", "酒红"], input.Source.Facts
            .Where(fact => fact.Source == "normalization:color-options")
            .Select(fact => fact.Value));
        var unresolved = Assert.Single(input.Source.Facts,
            fact => fact.Source == "normalization:color-options-unresolved");
        Assert.Equal("6有现货", unresolved.Value);
        Assert.Equal(["f001"], unresolved.DerivedFromFactIds);
    }

    [Fact]
    public void Create_PreservesOnlyPluginVerifiedSkuCombinations()
    {
        var raw = JsonSerializer.SerializeToElement(new
        {
            skuMatrixStatus = "verified",
            skuDimensions = new object[]
            {
                new
                {
                    name = "颜色",
                    source = "attribute:颜色",
                    options = new[] { new { optionKey = "color:1", sourceValue = "1白色", normalizedValue = "白色", status = "normalized" } },
                },
                new
                {
                    name = "尺码",
                    source = "attribute:尺码",
                    options = new[] { new { optionKey = "size:1", sourceValue = "45", normalizedValue = "45", status = "normalized" } },
                },
            },
            skuCombinations = new[]
            {
                new
                {
                    combinationKey = "color:1|size:1",
                    verification = "dom-interaction",
                    options = new { color = "白色", colorSourceValue = "1白色", size = "45" },
                    price = 65.5m,
                    stock = 12,
                },
            },
        });
        var detail = new DetailCollectionResultDto(
            0, 1, "礼服裙", "https://detail.1688.com/offer/123456.html", "success",
            null, null, "礼服裙", [], [], [], Raw: raw);

        var input = FieldMatchingInputBuilder.Create("COL-1", "MAP-1", detail);

        Assert.Equal("verified", input.Source.SkuMatrixStatus);
        Assert.Equal(2, input.Source.SkuDimensions.Count);
        var combination = Assert.Single(input.Source.SkuCombinations);
        Assert.Equal("白色", combination.Options["color"]);
        Assert.Equal("45", combination.Options["size"]);
        Assert.Equal(65.5m, combination.Price);
        Assert.Equal(12, combination.Stock);
    }

    [Fact]
    public void SemanticRequest_UsesNormalizedInputAndOnlySelectedUnresolvedAttributes()
    {
        var detail = new DetailCollectionResultDto(
            0,
            1,
            "吊带裙",
            "https://detail.1688.com/offer/123456.html",
            "success",
            "https://detail.1688.com/offer/123456.html",
            "2026-09-17T00:00:00Z",
            "吊带裙",
            [new DetailFactDto("颜色", "白色", "dom-pair")],
            [],
            []);
        var schema = new OzonCategorySchema(
            10,
            20,
            DateTimeOffset.UtcNow,
            [
                new OzonAttributeDefinition(30, 0, "颜色", "", "String", false, true, 99, 1, ""),
                new OzonAttributeDefinition(31, 0, "名称", "", "String", false, true, 0, 1, ""),
            ]);
        var input = FieldMatchingInputBuilder.Create(
            "COL-1", "MAP-1", detail, schema, "服装 > 连衣裙", true);
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>> candidates =
            new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>
            {
                [30] = [new(9001, "白色")],
            };

        var request = SemanticMappingRequestFactory.CreateFromFieldMatchingInput(
            "REQ-1", input, candidates, new HashSet<long> { 30 });

        var target = Assert.Single(request.TargetAttributes);
        Assert.Equal(30, target.AttributeId);
        Assert.Equal(9001, Assert.Single(target.DictionaryCandidates).ValueId);
        Assert.Equal("f001", Assert.Single(request.SourceFacts).FactId);
        Assert.Empty(SemanticMappingResponseValidator.ValidateRequest(request));
    }
}
