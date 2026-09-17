using System.Text.Json;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Contracts.Tests;

public sealed class FieldMatchingFinalOutputMergerTests
{
    [Fact]
    public void Merge_DressCase_CombinesDeterministicQwenAndConversionResults()
    {
        var (input, plan) = BuildCase(
            200000933,
            93211,
            "女装 > 连衣裙",
            "2025夏季女装吊带连衣裙",
            [
                new DetailFactDto("商品名称", "2025夏季女装吊带连衣裙", "document.title"),
                new DetailFactDto("颜色", "象牙白", "dom-pair"),
                new DetailFactDto("尺码", "S、M", "dom-pair"),
            ],
            [
                Attribute(4180, "名称"),
                Attribute(8229, "类型", dictionaryId: 1960),
                Attribute(10096, "商品颜色", dictionaryId: 1494),
                Attribute(4295, "俄罗斯尺码", dictionaryId: 835, isCollection: true),
                Attribute(8292, "合并至一张卡片"),
            ],
            new Dictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>
            {
                [8229] = [new(93182, "Платье"), new(93211, "Сарафан")],
                [10096] = [new(700, "Белый"), new(701, "Молочный")],
            });
        var qwen = new SemanticMappingResponse(
            "REQ-DRESS",
            [new SemanticTargetMapping(
                10096,
                "商品颜色",
                SemanticMappingStatuses.Mapped,
                ["f002"],
                ["颜色"],
                ["象牙白"],
                ["Молочный"],
                [701],
                SemanticMappingMethods.SemanticLabel,
                0.94m,
                false,
                false,
                "象牙白与候选Молочный语义一致。")],
            ["f001", "f003"],
            []);
        var sizeBatch = RussianSizeRuleCatalog.ConvertMany(
            "women-upper-dress",
            [new RussianSizeSourceOption("sku-s", "S"), new RussianSizeSourceOption("sku-m", "M")]);
        FieldConversionDecision[] conversions =
        [RussianSizeConversionDecisionFactory.Create(
            4295,
            ["f003"],
            sizeBatch,
            [new(420, "42"), new(440, "44")])];

        var output = FieldMatchingFinalOutputMerger.Merge(input, plan, qwen, conversions);

        Assert.Equal(FieldMatchingProductStatuses.Mapped, output.Status);
        Assert.True(output.Validation.ReadyForNextStage);
        Assert.False(output.Validation.ReadyForListing);
        Assert.Equal(5, output.Validation.MappedRequiredCount);
        Assert.Equal(FieldMatchingDecisionSources.Deterministic,
            output.TargetMappings.Single(item => item.AttributeId == 8229).DecisionSource);
        Assert.Equal([93211L],
            output.TargetMappings.Single(item => item.AttributeId == 8229).FinalValue.DictionaryValueIds);
        Assert.Equal(FieldMatchingDecisionSources.Qwen,
            output.TargetMappings.Single(item => item.AttributeId == 10096).DecisionSource);
        Assert.Equal([701L],
            output.TargetMappings.Single(item => item.AttributeId == 10096).FinalValue.DictionaryValueIds);
        Assert.Equal(FieldMatchingDecisionSources.Conversion,
            output.TargetMappings.Single(item => item.AttributeId == 4295).DecisionSource);
        Assert.Equal([420L, 440L],
            output.TargetMappings.Single(item => item.AttributeId == 4295).FinalValue.DictionaryValueIds);
        Assert.Equal(2,
            output.TargetMappings.Single(item => item.AttributeId == 4295).ConversionTraces.Count);
    }

    [Fact]
    public void Merge_KeyboardAccessoryCase_CompletesWithDeterministicRulesOnly()
    {
        var (input, plan) = BuildCase(
            170000001,
            17001,
            "电脑配件 > 键帽",
            "PBT机械键盘键帽",
            [
                new DetailFactDto("商品名称", "PBT机械键盘键帽", "document.title"),
                new DetailFactDto("材质", "PBT", "dom-pair"),
                new DetailFactDto("接口类型", "十字轴", "dom-pair"),
            ],
            [
                Attribute(1, "名称"),
                Attribute(2, "材料"),
                Attribute(3, "接口类型"),
            ]);

        var output = FieldMatchingFinalOutputMerger.Merge(input, plan);

        Assert.Equal(FieldMatchingProductStatuses.Mapped, output.Status);
        Assert.True(output.Validation.ReadyForNextStage);
        Assert.All(output.TargetMappings, mapping =>
        {
            Assert.Equal(FieldMatchingFinalStatuses.Mapped, mapping.Status);
            Assert.Equal(FieldMatchingDecisionSources.Deterministic, mapping.DecisionSource);
        });
    }

    [Fact]
    public void Merge_PhoneCaseCase_BlocksWhenRequiredCompatibilityEvidenceIsMissing()
    {
        var (input, plan) = BuildCase(
            170000002,
            17002,
            "手机配件 > 手机壳",
            "透明TPU手机保护壳",
            [
                new DetailFactDto("商品名称", "透明TPU手机保护壳", "document.title"),
                new DetailFactDto("材质", "TPU", "dom-pair"),
            ],
            [
                Attribute(1, "名称"),
                Attribute(2, "材料"),
                Attribute(3, "兼容型号"),
            ]);

        var output = FieldMatchingFinalOutputMerger.Merge(input, plan);

        Assert.Equal(FieldMatchingProductStatuses.Blocked, output.Status);
        Assert.False(output.Validation.ReadyForNextStage);
        Assert.Equal(1, output.Validation.MissingRequiredCount);
        var compatibility = output.TargetMappings.Single(mapping => mapping.AttributeId == 3);
        Assert.Equal(FieldMatchingFinalStatuses.MissingEvidence, compatibility.Status);
        Assert.True(compatibility.RequiresHumanReview);
    }

    [Fact]
    public void Merge_RejectsQwenAttemptToOverrideDeterministicDecision()
    {
        var (input, plan) = BuildCase(
            10,
            20,
            "家居 > 水杯",
            "玻璃水杯",
            [new DetailFactDto("材质", "玻璃", "dom-pair")],
            [Attribute(1, "材料")]);
        var qwen = new SemanticMappingResponse(
            "REQ",
            [new SemanticTargetMapping(
                1, "材料", SemanticMappingStatuses.Mapped,
                ["f001"], ["材质"], ["玻璃"], ["塑料"], [],
                SemanticMappingMethods.SemanticLabel, 0.9m, false, false, "模型改写")],
            [],
            []);

        var error = Assert.Throws<ArgumentException>(() =>
            FieldMatchingFinalOutputMerger.Merge(input, plan, qwen));

        Assert.Contains("未授权目标字段", error.Message);
    }

    [Fact]
    public void Merge_RejectsFabricatedConversionDictionaryValueId()
    {
        var (input, plan) = BuildCase(
            10,
            20,
            "女装 > 上衣",
            "女装上衣",
            [new DetailFactDto("尺码", "M", "dom-pair")],
            [Attribute(4295, "俄罗斯尺码", dictionaryId: 835)]);
        FieldConversionDecision[] conversions =
        [
            new(4295, FieldConversionStatuses.Mapped, ["f001"], ["44"],
                [new(440, "44")], [999], "test-rule", 1m, "伪造ID", []),
        ];

        var error = Assert.Throws<ArgumentException>(() =>
            FieldMatchingFinalOutputMerger.Merge(input, plan, conversionResults: conversions));

        Assert.Contains("候选范围之外", error.Message);
    }

    [Fact]
    public void Merge_SerializedOutputOmitsUiOnlyDisplayProperties()
    {
        var (input, plan) = BuildCase(
            10, 20, "家居 > 水杯", "玻璃水杯",
            [new DetailFactDto("材质", "玻璃", "dom-pair")],
            [Attribute(1, "材料")]);

        var json = JsonSerializer.Serialize(FieldMatchingFinalOutputMerger.Merge(input, plan));

        Assert.DoesNotContain("FinalTextDisplay", json, StringComparison.Ordinal);
        Assert.DoesNotContain("FinalDictionaryValueIdDisplay", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_ReportsDroppedSizeAsWarningWithoutBlockingMappedKnownSizes()
    {
        var (input, plan) = BuildCase(
            10, 20, "女装 > 连衣裙", "女士连衣裙",
            [new DetailFactDto("尺码", "XS、S", "dom-pair")],
            [Attribute(4295, "俄罗斯尺码", dictionaryId: 835, isCollection: true)]);
        var batch = RussianSizeRuleCatalog.ConvertMany(
            "women-upper-dress",
            [new RussianSizeSourceOption("xs", "XS"), new RussianSizeSourceOption("s", "S")]);
        var conversion = RussianSizeConversionDecisionFactory.Create(
            4295, ["f001"], batch, [new SemanticDictionaryCandidate(420, "42")], true);

        var output = FieldMatchingFinalOutputMerger.Merge(input, plan, conversionResults: [conversion]);

        Assert.Equal(FieldMatchingProductStatuses.Mapped, output.Status);
        Assert.Contains(output.Warnings, warning => warning.Contains("XS", StringComparison.Ordinal));
        var excluded = Assert.Single(output.ExcludedSourceOptions);
        Assert.Equal("XS", excluded.SourceValue);
        Assert.Contains("排除", excluded.Reason, StringComparison.Ordinal);
        var size = Assert.Single(output.TargetMappings);
        Assert.Equal([420L], size.FinalValue.DictionaryValueIds);
        Assert.Equal(FieldConversionStatuses.Dropped,
            size.ConversionTraces.Single(trace => trace.SourceValue == "XS").Status);
    }

    private static (FieldMatchingInput Input, FieldMatchingEnginePlan Plan) BuildCase(
        long categoryId,
        long typeId,
        string categoryPath,
        string title,
        IReadOnlyList<DetailFactDto> facts,
        IReadOnlyList<OzonAttributeDefinition> attributes,
        IReadOnlyDictionary<long, IReadOnlyList<SemanticDictionaryCandidate>>? candidates = null)
    {
        var detail = new DetailCollectionResultDto(
            0,
            1,
            title,
            $"https://detail.1688.com/offer/{categoryId}{typeId}.html",
            "success",
            $"https://detail.1688.com/offer/{categoryId}{typeId}.html",
            "2026-09-17T00:00:00Z",
            title,
            facts,
            [],
            []);
        var schema = new OzonCategorySchema(categoryId, typeId, DateTimeOffset.UtcNow, attributes);
        var input = FieldMatchingInputBuilder.Create(
            $"COL-{categoryId}", $"MAP-{categoryId}", detail, schema, categoryPath, true);
        var snapshot = new DetailFactSnapshotDto(
            detail.DetailUrl!, detail.CapturedAt!, detail.PageTitle, detail.Facts, null);
        var coverage = AttributeCoverageResolver.Resolve(schema, snapshot);
        var plan = FieldMatchingEnginePlanBuilder.Create(input, coverage, candidates);
        return (input, plan);
    }

    private static OzonAttributeDefinition Attribute(
        long id,
        string name,
        long dictionaryId = 0,
        bool isCollection = false) =>
        new(id, 0, name, string.Empty, "String", isCollection, true, dictionaryId, isCollection ? 20 : 1, "Основные");
}
