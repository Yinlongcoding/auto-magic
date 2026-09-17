using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;

namespace AutoMagic.Contracts.Tests;

public sealed class SemanticDictionaryCandidateResolverTests
{
    [Fact]
    public void Resolve_UsesExactNormalizedMatchesOnly()
    {
        var result = SemanticDictionaryCandidateResolver.Resolve(
            [" 黑色 ", "红色"],
            [
                new OzonDictionaryValue(3, "黑色", string.Empty, string.Empty),
                new OzonDictionaryValue(4, "红色", string.Empty, string.Empty),
                new OzonDictionaryValue(5, "深黑色", string.Empty, string.Empty),
            ]);

        Assert.Equal([3L, 4L], result.Select(candidate => candidate.ValueId));
    }

    [Fact]
    public void Resolve_DoesNotInventCandidateWhenNoExactMatchExists()
    {
        var result = SemanticDictionaryCandidateResolver.Resolve(
            ["蓝色"],
            [new OzonDictionaryValue(3, "图片色", string.Empty, string.Empty)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_PreservesOzonCandidateMetadataForAuditing()
    {
        var result = SemanticDictionaryCandidateResolver.Resolve(
            ["氨纶"],
            [new OzonDictionaryValue(3, "氨纶", "材料说明", "https://example.test/material.png")]);

        var candidate = Assert.Single(result);
        Assert.Equal("材料说明", candidate.Info);
        Assert.Equal("https://example.test/material.png", candidate.Picture);
    }
}
