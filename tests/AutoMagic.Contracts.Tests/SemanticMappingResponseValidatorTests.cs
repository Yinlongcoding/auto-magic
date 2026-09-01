using System.Text.Json;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Contracts.Tests;

public sealed class SemanticMappingResponseValidatorTests
{
    [Fact]
    public void CreateRequiredAttributeRequest_BuildsDeterministicFactsAndRequiredTargets()
    {
        var schema = new OzonCategorySchema(
            200000933,
            93211,
            DateTimeOffset.UtcNow,
            [
                Attribute(9163, "性别", required: true, dictionaryId: 320),
                Attribute(100, "可选说明", required: false),
            ]);
        var snapshot = new DetailFactSnapshotDto(
            "https://detail.1688.com/offer/1060627180703.html",
            "2026-09-01T12:13:38.007Z",
            "蓝色连衣裙女夏",
            [new DetailFactDto("尺码", "S、M、L、XL", "normal-attributes")],
            null);

        var request = SemanticMappingRequestFactory.CreateRequiredAttributeRequest(
            "request-001",
            "服装 > 服装 > 无袖连衣裙",
            schema,
            snapshot);

        Assert.Equal("1060627180703", request.MappingContext.OfferId);
        Assert.Equal(2, request.MappingContext.SourceFactCount);
        Assert.False(request.MappingContext.DictionaryCandidatesProvided);
        Assert.Equal("f001", request.SourceFacts[0].FactId);
        Assert.Equal("商品名称", request.SourceFacts[0].Label);
        Assert.Equal("document.title", request.SourceFacts[0].Source);
        Assert.Equal("f002", request.SourceFacts[1].FactId);
        Assert.Equal("尺码", request.SourceFacts[1].Label);
        Assert.Single(request.TargetAttributes);
        Assert.Equal(9163, request.TargetAttributes[0].AttributeId);
    }

    [Fact]
    public void ParseAndValidate_AcceptsContractCompliantResponse()
    {
        var request = CreateRequest();
        var response = CreateValidResponse();
        var json = JsonSerializer.Serialize(response, SemanticMappingJson.StrictOptions);

        var result = SemanticMappingResponseValidator.ParseAndValidate(request, json);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Response);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ParseAndValidate_RejectsUnknownJsonMembers()
    {
        var request = CreateRequest();
        var validJson = JsonSerializer.Serialize(CreateValidResponse(), SemanticMappingJson.StrictOptions);
        var json = validJson.Insert(1, "\"unexpected\":true,");

        var result = SemanticMappingResponseValidator.ParseAndValidate(request, json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "json.invalid");
    }

    [Fact]
    public void ValidateResponse_RejectsFabricatedOrChangedEvidence()
    {
        var request = CreateRequest();
        var response = CreateValidResponse();
        var invalidGender = response.TargetMappings[0] with
        {
            SourceFactIds = ["f999"],
            SourceLabels = ["商品名称"],
            SourceValues = ["伪造值"],
        };
        response = response with
        {
            TargetMappings = [invalidGender, .. response.TargetMappings.Skip(1)],
            UnmappedSourceFactIds = ["f001"],
        };

        var result = SemanticMappingResponseValidator.ValidateResponse(request, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "mapping.fabricatedFact");
    }

    [Fact]
    public void ValidateResponse_RejectsIncorrectUnmappedComplement()
    {
        var request = CreateRequest();
        var response = CreateValidResponse() with { UnmappedSourceFactIds = ["f003"] };

        var result = SemanticMappingResponseValidator.ValidateResponse(request, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "response.unmappedComplement");
    }

    [Fact]
    public void ValidateResponse_RejectsPolicyStateWithoutExternalDependency()
    {
        var request = CreateRequest();
        var response = CreateValidResponse();
        var invalidPolicy = response.TargetMappings[1] with { ExternalRuleRequired = false };
        response = response with
        {
            TargetMappings = [response.TargetMappings[0], invalidPolicy, .. response.TargetMappings.Skip(2)],
        };

        var result = SemanticMappingResponseValidator.ValidateResponse(request, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "mapping.policy");
    }

    [Fact]
    public void ValidateResponse_AllowsOnlyProvidedDictionaryValueIds()
    {
        var request = CreateRequest();
        var genderTarget = request.TargetAttributes[0] with
        {
            DictionaryCandidates = [new SemanticDictionaryCandidate(9001, "女性")],
        };
        request = request with
        {
            MappingContext = request.MappingContext with { DictionaryCandidatesProvided = true },
            TargetAttributes = [genderTarget, .. request.TargetAttributes.Skip(1)],
        };
        var response = CreateValidResponse();
        var mappedGender = response.TargetMappings[0] with
        {
            Status = SemanticMappingStatuses.Mapped,
            SelectedDictionaryValueIds = [9001],
            DictionaryResolutionRequired = false,
        };
        response = response with
        {
            TargetMappings = [mappedGender, .. response.TargetMappings.Skip(1)],
        };

        var validResult = SemanticMappingResponseValidator.ValidateResponse(request, response);
        var fabricatedResponse = response with
        {
            TargetMappings =
            [
                mappedGender with { SelectedDictionaryValueIds = [9999] },
                .. response.TargetMappings.Skip(1),
            ],
        };
        var fabricatedResult = SemanticMappingResponseValidator.ValidateResponse(
            request,
            fabricatedResponse);

        Assert.True(validResult.IsValid);
        Assert.False(fabricatedResult.IsValid);
        Assert.Contains(
            fabricatedResult.Issues,
            issue => issue.Code == "mapping.fabricatedDictionaryId");
    }

    private static SemanticMappingRequest CreateRequest() =>
        new(
            "request-001",
            SemanticMappingPurposes.EvaluateCandidates,
            new SemanticMappingContext(
                "1688",
                "Ozon",
                200000933,
                93211,
                "服装 > 服装 > 无袖连衣裙",
                true,
                "1060627180703",
                "https://detail.1688.com/offer/1060627180703.html",
                "2026-09-01T12:13:38.007Z",
                3,
                false),
            [
                Target(9163, "性别", dictionaryId: 320),
                Target(8292, "合并至一张卡片"),
                Target(31, "服装和鞋类品牌", dictionaryId: 28732849),
                Target(4295, "俄罗斯尺码", dictionaryId: 835),
            ],
            [
                new SemanticSourceFact("f001", "商品名称", "蓝色连衣裙女夏", "document.title"),
                new SemanticSourceFact("f002", "尺码", "S、M、L、XL", "normal-attributes"),
                new SemanticSourceFact("f003", "品牌", "其它", "normal-attributes"),
            ],
            SemanticMappingOutputContract.Default);

    private static SemanticMappingResponse CreateValidResponse() =>
        new(
            "request-001",
            [
                new SemanticTargetMapping(
                    9163,
                    "性别",
                    SemanticMappingStatuses.DictionaryPending,
                    ["f001"],
                    ["商品名称"],
                    ["蓝色连衣裙女夏"],
                    ["女性"],
                    [],
                    SemanticMappingMethods.ExplicitTitle,
                    0.95m,
                    true,
                    false,
                    "标题明确写出女。"),
                new SemanticTargetMapping(
                    8292,
                    "合并至一张卡片",
                    SemanticMappingStatuses.PolicyRequired,
                    [],
                    [],
                    [],
                    [],
                    [],
                    SemanticMappingMethods.Policy,
                    0.9m,
                    false,
                    true,
                    "需要应用提供合并策略。"),
                new SemanticTargetMapping(
                    31,
                    "服装和鞋类品牌",
                    SemanticMappingStatuses.Ambiguous,
                    ["f003"],
                    ["品牌"],
                    ["其它"],
                    ["无品牌"],
                    [],
                    SemanticMappingMethods.SemanticLabel,
                    0.5m,
                    true,
                    false,
                    "其它是占位值，只能作为待确认候选。"),
                new SemanticTargetMapping(
                    4295,
                    "俄罗斯尺码",
                    SemanticMappingStatuses.ConversionRequired,
                    ["f002"],
                    ["尺码"],
                    ["S、M、L、XL"],
                    [],
                    [],
                    SemanticMappingMethods.Conversion,
                    0.95m,
                    true,
                    true,
                    "需要外部俄罗斯尺码换算表。"),
            ],
            [],
            ["俄罗斯尺码缺少换算表。"]);

    private static SemanticTargetAttribute Target(long id, string name, long dictionaryId = 0) =>
        new(
            id,
            0,
            name,
            string.Empty,
            "String",
            false,
            true,
            1,
            dictionaryId,
            []);

    private static OzonAttributeDefinition Attribute(
        long id,
        string name,
        bool required,
        long dictionaryId = 0) =>
        new(id, 0, name, string.Empty, "String", false, required, dictionaryId, 1, string.Empty);
}
