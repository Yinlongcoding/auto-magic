using AutoMagic.Application.Ozon;
using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Contracts.Tests;

public sealed class AttributeCoverageResolverTests
{
    [Fact]
    public void Resolve_UsesEvidenceAndKeepsDictionaryAttributesPending()
    {
        var schema = new OzonCategorySchema(
            10,
            20,
            DateTimeOffset.UtcNow,
            [
                Attribute(1, "Материал", required: true),
                Attribute(2, "Бренд", required: true, dictionaryId: 99),
                Attribute(3, "Срок годности", required: true),
            ]);
        var snapshot = new DetailFactSnapshotDto(
            "https://detail.1688.com/offer/1.html",
            "2026-08-31T00:00:00Z",
            "示例",
            [
                new DetailFactDto("材质", "棉", "dom-pair"),
                new DetailFactDto("品牌", "示例牌", "embedded-json"),
            ],
            null);

        var report = AttributeCoverageResolver.Resolve(schema, snapshot);

        Assert.Equal(AttributeResolutionStatuses.Resolved, report.Attributes[0].Status);
        Assert.Equal("棉", report.Attributes[0].SourceValue);
        Assert.Equal(AttributeResolutionStatuses.DictionaryValueRequired, report.Attributes[1].Status);
        Assert.Equal(AttributeResolutionStatuses.Missing, report.Attributes[2].Status);
        Assert.Equal(1, report.ReadyRequiredCount);
        Assert.Equal(1, report.DictionaryRequiredCount);
        Assert.Equal(1, report.MissingRequiredCount);
        Assert.False(report.IsReadyForSubmission);
    }

    [Fact]
    public void Resolve_MatchesChineseMarketplaceLabelsAndInfersGenderFromTitle()
    {
        var schema = new OzonCategorySchema(
            10,
            20,
            DateTimeOffset.UtcNow,
            [
                Attribute(1, "性别", required: true, dictionaryId: 10),
                Attribute(2, "商品颜色", required: true, dictionaryId: 11),
                Attribute(3, "服装和鞋类品牌", required: true, dictionaryId: 12),
                Attribute(4, "裙子/连衣裙长度", required: false),
                Attribute(5, "衣领", required: false),
                Attribute(6, "套筒类型", required: false),
            ]);
        var snapshot = new DetailFactSnapshotDto(
            "https://detail.1688.com/offer/2.html",
            "2026-09-01T00:00:00Z",
            "2026夏季新款法式收腰女装连衣裙",
            [
                new DetailFactDto("颜色", "黑色, 白色", "dom-pair"),
                new DetailFactDto("品牌", "示例牌", "dom-pair"),
                new DetailFactDto("裙长", "中长裙", "dom-pair"),
                new DetailFactDto("领型", "V领", "dom-pair"),
                new DetailFactDto("袖型", "常规袖", "dom-pair"),
            ],
            null);

        var report = AttributeCoverageResolver.Resolve(schema, snapshot);

        Assert.All(report.Attributes.Take(3), resolution =>
            Assert.Equal(AttributeResolutionStatuses.DictionaryValueRequired, resolution.Status));
        Assert.Equal("女", report.Attributes[0].SourceValue);
        Assert.Equal("inference:title", report.Attributes[0].Source);
        Assert.Equal(0.92m, report.Attributes[0].Confidence);
        Assert.Equal(AttributeMatchMethods.Alias, report.Attributes[1].MatchMethod);
        Assert.Equal(AttributeMatchMethods.Alias, report.Attributes[2].MatchMethod);
        Assert.All(report.Attributes.Skip(3), resolution =>
            Assert.Equal(AttributeResolutionStatuses.Resolved, resolution.Status));
        Assert.Equal(0, report.MissingRequiredCount);
        Assert.Equal(0, report.ReviewRequiredCount);
        Assert.Equal(3, report.DictionaryRequiredCount);
    }

    [Fact]
    public void Resolve_PreservesLowConfidenceCandidateForReviewWithoutAutoUsingIt()
    {
        var schema = new OzonCategorySchema(
            10,
            20,
            DateTimeOffset.UtcNow,
            [Attribute(1, "面料主要成分", required: true)]);
        var snapshot = new DetailFactSnapshotDto(
            "https://detail.1688.com/offer/3.html",
            "2026-09-01T00:00:00Z",
            "示例商品",
            [new DetailFactDto("主面料成分", "聚酯纤维", "dom-pair")],
            null);

        var report = AttributeCoverageResolver.Resolve(schema, snapshot);
        var resolution = Assert.Single(report.Attributes);

        Assert.Equal(AttributeResolutionStatuses.ReviewRequired, resolution.Status);
        Assert.Equal(AttributeMatchMethods.BigramCandidate, resolution.MatchMethod);
        Assert.Equal("主面料成分", resolution.SourceLabel);
        Assert.Equal("聚酯纤维", resolution.SourceValue);
        Assert.InRange(resolution.Confidence, 0.70m, 0.89m);
        Assert.Equal(1, report.ReviewRequiredCount);
        Assert.Equal(0, report.MissingRequiredCount);
        Assert.False(report.IsReadyForSubmission);
    }

    private static OzonAttributeDefinition Attribute(
        long id,
        string name,
        bool required,
        long dictionaryId = 0) =>
        new(id, 0, name, string.Empty, "String", false, required, dictionaryId, 1, string.Empty);
}
