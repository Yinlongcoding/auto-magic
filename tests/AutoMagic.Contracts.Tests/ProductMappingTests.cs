using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;

namespace AutoMagic.Contracts.Tests;

public sealed class ProductMappingTests
{
    [Fact]
    public void Input_UsesAllSchemaAttributesAndOnlyRawFactsAndVerifiedRealSku()
    {
        var input = Fixture.Input();
        var source = input.Source with
        {
            Facts = [.. input.Source.Facts,
                new("derived", "attribute", "原产国", "China", "policy:default-origin", ""),
                new("gender", "derived", "性别", "女", "inference:title", "$.derivedFacts.gender"),
                new("sizes", "derived", "尺码", "S/M/L", "structured-sku-dimension", "$.raw.skuDimensions")],
            SkuCombinations = [.. input.Source.SkuCombinations,
                new("fake-blue-S", "unverified", new Dictionary<string, string?> { ["颜色"] = "蓝色" }, null, null)],
        };
        var built = ProductMappingInputBuilder.Create(input with { Source = source });
        Assert.Equal(3, built.Request.Skus.Count);
        Assert.Contains(built.Request.Attributes, a => !a.IsRequired && a.AttributeId == 30);
        Assert.DoesNotContain(built.Request.Facts, f => f.Value == "China");
        Assert.DoesNotContain(built.Request.Facts, f => f.FactId is "product:gender" or "product:sizes");
        Assert.Contains(built.SourceIssues, i => i.Code == "sku.unconfirmed");
        Assert.DoesNotContain(built.Request.Skus, s => s.VariantKey == "sku:fake-blue-S");
        var json = System.Text.Json.JsonSerializer.Serialize(built.Request);
        Assert.DoesNotContain("SHOULD_NOT_BE_SENT", json);
    }

    [Fact]
    public void Input_DoesNotExcludeRealOutOfStockSkuOrManufactureMissingSku()
    {
        var input = Fixture.Input();
        var built = ProductMappingInputBuilder.Create(input);
        Assert.Equal(3, built.Request.Skus.Count); // Red/S, Red/M, Blue/M, never Blue/S.
        Assert.Contains(built.Request.Skus, s => s.SourceSkuId == "source-3"); // stock=0
        var empty = ProductMappingInputBuilder.Create(input with { Source = input.Source with { SkuCombinations = [] } });
        Assert.Empty(empty.Request.Skus);
        Assert.Contains(empty.SourceIssues, i => i.Code == "sku.none_confirmed");
    }

    [Fact]
    public async Task Input_BlocksAiWhenSelectedSleeveTypeClearlyConflictsWithSourceFact()
    {
        var input = Fixture.Input();
        input = input with
        {
            Source = input.Source with
            {
                Facts = [.. input.Source.Facts,
                    new("sleeve", "attribute", "袖长", "长袖", "detail", "$.facts.sleeve")],
            },
            Target = input.Target with { CategoryPath = "服装 > 服装 > 无袖连衣裙" },
        };
        var built = ProductMappingInputBuilder.Create(input);
        var mapper = new Fixture.Mapper(request => Fixture.Response(request));

        var run = await new ProductMappingRunner(mapper, new Fixture.Dictionary()).RunAsync(
            built, new("test"), new("client", "key"), null, CancellationToken.None);

        Assert.Contains(run.Validation.Issues, issue => issue.Code == "category.type_conflict");
        Assert.Empty(mapper.Requests);
        Assert.Empty(run.Calls);
        Assert.Null(run.Response);
    }

    [Fact]
    public void Input_KeepsStructurallyValidObservedSkuValuesWithoutCategoryGuessing()
    {
        var input = Fixture.Input();
        FieldMatchingSkuCombination Sku(string key, string id, string color, string size, long stock) =>
            new(key, "structured-json",
                new Dictionary<string, string?> { ["颜色"] = color, ["尺码"] = size }, 35m, stock)
            { SkuId = id, Availability = stock == 0 ? "unavailable" : "available" };
        input = input with { Source = input.Source with
        {
            SkuCombinations =
            [
                Sku("black-M", "valid-zero", "黑色", "M", 0),
                Sku("yellow-one", "invalid-one", "黄色", "均码", 10),
                Sku("number-nine", "invalid-nine", "9号", "均码", 0),
                Sku("number-eleven", "invalid-eleven", "11号", "大码", 10),
            ],
        } };

        var built = ProductMappingInputBuilder.Create(input);

        Assert.Equal(4, built.Request.Skus.Count);
        Assert.Contains(built.Request.Skus, sku => sku.SourceSkuId == "valid-zero" &&
            sku.Options["颜色"] == "黑色" && sku.Options["尺码"] == "M");
        Assert.Contains(built.Request.Skus, sku => sku.SourceSkuId == "invalid-one" && sku.Options["尺码"] == "均码");
        Assert.Contains(built.Request.Skus, sku => sku.SourceSkuId == "invalid-nine" && sku.Options["颜色"] == "9号");
        Assert.Contains(built.Request.Skus, sku => sku.SourceSkuId == "invalid-eleven" && sku.Options["尺码"] == "大码");
        Assert.DoesNotContain(built.SourceIssues, issue => issue.Code == "sku.invalid_option");
    }

    [Fact]
    public void Input_CreatesOneProductGroupAndSixStableMerchantSkusForObservedVariants()
    {
        var input = Fixture.Input();
        var combinations = new[] { "黑色", "白色", "蓝色" }
            .SelectMany(color => new[] { "M", "L" }.Select(size =>
                new FieldMatchingSkuCombination($"{color}-{size}", "structured-json",
                    new Dictionary<string, string?> { ["颜色"] = color, ["尺码"] = size }, 35m, 10)
                { SkuId = $"sku-{color}-{size}" }))
            .ToArray();
        input = input with
        {
            ProductRef = input.ProductRef with { OfferId = "834198976463" },
            Source = input.Source with { Title = "男士纯棉商务POLO衫", SkuCombinations = combinations },
        };

        var request = ProductMappingInputBuilder.Create(input, "polo-test").Request;

        Assert.Equal("1688:834198976463", request.ProductGroupKey);
        Assert.Equal(6, request.Skus.Count);
        Assert.Equal(6, request.Skus.Select(sku => sku.VariantKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, request.Skus.Select(sku => sku.MerchantSku).Distinct(StringComparer.Ordinal).Count());
        Assert.All(request.Skus, sku =>
        {
            Assert.StartsWith("AM-1688-834198976463-", sku.MerchantSku, StringComparison.Ordinal);
            Assert.InRange(sku.MerchantSku.Length, 1, ProductVariantIdentityFactory.MerchantSkuMaximumLength);
            Assert.Equal("source-sku-id", sku.IdentityStrategy);
            Assert.Equal(2, sku.Options.Count);
        });
        var plan = new ProductMappingRun(request, null, new(false, 0, [], []), []).SkuIdentityPlan;
        Assert.Equal(request.ProductGroupKey, plan.ProductGroupKey);
        Assert.Equal(6, plan.Variants.Count);
    }

    [Fact]
    public void VariantIdentity_IsOrderIndependentAndPrefersSourceOptionIdsBeforeTexts()
    {
        var first = ProductVariantIdentityFactory.Create("123", null,
            new Dictionary<string, string?> { ["颜色"] = "黑色", ["尺码"] = "M" },
            new Dictionary<string, string?> { ["颜色"] = "11", ["尺码"] = "21" },
            new Dictionary<string, string?> { ["颜色"] = "1", ["尺码"] = "2" });
        var reordered = ProductVariantIdentityFactory.Create("123", null,
            new Dictionary<string, string?> { ["尺码"] = "Medium", ["颜色"] = "Black" },
            new Dictionary<string, string?> { ["尺码"] = "21", ["颜色"] = "11" },
            new Dictionary<string, string?> { ["尺码"] = "2", ["颜色"] = "1" });

        Assert.Equal("source-option-ids", first.IdentityStrategy);
        Assert.Equal(first.VariantKey, reordered.VariantKey);
        Assert.Equal(first.MerchantSku, reordered.MerchantSku);

        var textOnly = ProductVariantIdentityFactory.Create("123", null,
            new Dictionary<string, string?> { ["颜色"] = "1黑色有现货", ["尺码"] = "M无货" });
        var cleanText = ProductVariantIdentityFactory.Create("123", null,
            new Dictionary<string, string?> { ["尺码"] = "M", ["颜色"] = "黑色" });
        Assert.Equal("source-option-values", textOnly.IdentityStrategy);
        Assert.Equal(cleanText.VariantKey, textOnly.VariantKey);
        Assert.Equal(cleanText.MerchantSku, textOnly.MerchantSku);
    }

    [Fact]
    public void Input_AcceptsObservedDomSkuAndPreservesRawColorEvidenceAndSourcePath()
    {
        var input = Fixture.Input();
        input = input with { Source = input.Source with
        {
            SkuDimensions = [new("颜色", "dom", []), new("尺码", "dom", [])],
            SkuCombinations = [new("color:1|size:1", "dom-interaction",
                new Dictionary<string, string?> { ["color"] = "红色", ["colorSourceValue"] = "经典红", ["size"] = "S" },
                null, null)],
        } };
        var result = ProductMappingInputBuilder.Create(input);
        var sku = Assert.Single(result.Request.Skus);
        Assert.Equal("经典红", sku.Options["颜色"]);
        Assert.Equal("S", sku.Options["尺码"]);
        Assert.Equal(2, sku.FactIds.Count);
        var color = Assert.Single(result.Request.Facts, f => f.ScopeKey == sku.VariantKey && f.Label == "颜色");
        Assert.Equal("经典红", color.Value);
        Assert.Contains("colorSourceValue", color.SourcePath);
        Assert.DoesNotContain(result.SourceIssues, i => i.Code == "sku.unconfirmed");
    }

    [Fact]
    public void Input_DuplicateSkuIdentityIsNotSilentlyDeduplicatedOrMapped()
    {
        var input = Fixture.Input();
        var duplicate = input.Source.SkuCombinations[0];
        var built = ProductMappingInputBuilder.Create(input with
        {
            Source = input.Source with { SkuCombinations = [duplicate, duplicate] },
        });
        Assert.Empty(built.Request.Skus);
        Assert.Equal(2, built.SourceIssues.Count(i => i.Code == "sku.unconfirmed"));
    }

    [Fact]
    public void Input_RequiresSelectedCategoryAndSnapshotsChangeWhenFactsChange()
    {
        var input = Fixture.Input();
        Assert.Throws<ArgumentException>(() => ProductMappingInputBuilder.Create(input with
        { Target = input.Target with { CategoryAndTypeConfirmedByUser = false } }));
        var one = ProductMappingInputBuilder.Create(input);
        var two = ProductMappingInputBuilder.Create(input);
        Assert.Equal(one.Request.InputFingerprint, two.Request.InputFingerprint);
        var changed = ProductMappingInputBuilder.Create(input with
        { Source = input.Source with { Title = "另一件商品" } });
        Assert.NotEqual(one.Request.InputFingerprint, changed.Request.InputFingerprint);
    }

    [Fact]
    public void FirstPassNormalizer_DropsInventedDictionaryIdsCanonicalizesUniqueEvidenceAndDefersPolicy()
    {
        var request = Fixture.Request() with
        {
            Attributes = [.. Fixture.Request().Attributes,
                new(8292, 0, "合并至一张卡片", "", "String", false, true, 1, 0, [])],
        };
        request = request with { Attributes = request.Attributes.Select(a => a.AttributeId == 20
            ? a with { DictionaryCandidates = [] } : a).ToArray() };
        var response = Fixture.Response(request, true) with
        {
            ProductMappings = [Fixture.Response(request, true).ProductMappings[0] with { EvidenceFactIds = ["f001"] },
                .. Fixture.Response(request, true).ProductMappings.Skip(1),
                new(8292, null, "suggested", [new("source-offer", null)], ["f001"], "使用源商品。")],
            Variants = Fixture.Response(request, true).Variants.Select((v, index) => index == 0
                ? v with { Mappings = [v.Mappings[0] with
                    { Status = "suggested", Values = [new("красный", 999)] }] }
                : v).ToArray(),
        };
        var normalized = ProductMappingResponseNormalizer.Normalize(request, response);
        var color = normalized.Response.Variants[0].Mappings[0];
        Assert.Equal("dictionary_pending", color.Status);
        Assert.Null(color.Values[0].DictionaryValueId);
        Assert.Equal(Fixture.Response(request, true).Variants[0].Mappings[0].EvidenceFactIds[0],
            color.EvidenceFactIds[0]);
        Assert.Equal("product:f001", normalized.Response.ProductMappings[0].EvidenceFactIds[0]);
        var grouping = normalized.Response.ProductMappings.Single(m => m.AttributeId == 8292);
        Assert.Equal("policy_required", grouping.Status);
        Assert.Empty(grouping.Values);
        Assert.Empty(grouping.EvidenceFactIds);
        Assert.Contains(normalized.Issues, i => i.Code == "dictionary.first_pass_normalized");
        Assert.Contains(normalized.Issues, i => i.Code == "evidence.canonicalized");
        Assert.Contains(normalized.Issues, i => i.Code == "policy.deferred");
    }

    [Fact]
    public async Task Runner_ContinuesSafeDictionaryResolutionWhenAnotherFirstPassRowIsInvalid()
    {
        var input = ProductMappingInputBuilder.Create(Fixture.Input(), "test");
        var mapper = new Fixture.Mapper(request =>
        {
            var response = Fixture.Response(request, request.Attributes.Single(a => a.AttributeId == 20).DictionaryCandidates.Count == 0);
            return response with { ProductMappings = [.. response.ProductMappings,
                new(999, null, "suggested", [new("bad", null)], ["product:f001"], "无效属性。") ] };
        });
        var dictionaries = new Fixture.Dictionary();
        var result = await new ProductMappingRunner(mapper, dictionaries).RunAsync(input,
            new("test-key"), new("client", "ozon-key"), null, CancellationToken.None);
        Assert.Equal(2, mapper.Requests.Count);
        Assert.Equal(2, dictionaries.Queries.Count);
        Assert.Contains(result.Validation.Issues, i => i.Code == "attribute.fabricated");
        Assert.All(result.Response!.Variants, v => Assert.Equal("suggested", v.Mappings[0].Status));
    }

    [Fact]
    public void Validation_CombinesSharedFactsWithEachActualSkuAndAllowsSupportedOptionalValues()
    {
        var request = Fixture.Request();
        var response = Fixture.Response(request);
        var validation = ProductMappingValidator.Validate(request, response);
        Assert.True(validation.ContractValid);
        Assert.Equal(0, validation.UnresolvedRequiredCount);
        Assert.Contains(validation.Rows, r => r.AttributeId == 30 && r.ValueDisplay == "повседневный");
        Assert.Equal(3, validation.Rows.Count(r => r.ScopeKey != "product" && r.AttributeId == 20));
        Assert.All(validation.Rows, r => Assert.Equal("约束通过·待语义复核", r.CheckDisplay));
        Assert.False(new ProductMappingRun(request, response, validation, []).ReadyForListing);
        Assert.Contains(validation.Issues, i => i.Code == "sku.same_suggestions");
    }

    [Theory]
    [InlineData("wrong-sku", "evidence.wrong_sku")]
    [InlineData("invented-fact", "evidence.fabricated")]
    [InlineData("no-fact", "evidence.required")]
    [InlineData("invented-id", "dictionary.invalid")]
    [InlineData("wrong-text", "dictionary.invalid")]
    [InlineData("too-many", "values.cardinality")]
    [InlineData("wrong-attribute", "attribute.fabricated")]
    public void Validation_RejectsInvalidSuggestionsEvenWhenModelSaysSuggested(string kind, string expectedCode)
    {
        var request = Fixture.Request();
        var response = Fixture.Response(request);
        var first = response.Variants[0];
        var mapping = first.Mappings[0];
        mapping = kind switch
        {
            "wrong-sku" => mapping with { EvidenceFactIds = [request.Skus[2].FactIds[0]] },
            "invented-fact" => mapping with { EvidenceFactIds = ["nonexistent"] },
            "no-fact" => mapping with { EvidenceFactIds = [] },
            "invented-id" => mapping with { Values = [new("красный", 999)] },
            "wrong-text" => mapping with { Values = [new("синий", 101)] },
            "too-many" => mapping with { Values = [new("красный", 101), new("синий", 102)] },
            _ => mapping with { AttributeId = 999 },
        };
        response = response with { Variants = [first with { Mappings = [mapping] }, .. response.Variants.Skip(1)] };
        var result = ProductMappingValidator.Validate(request, response);
        Assert.False(result.ContractValid);
        Assert.Contains(result.Issues, i => i.Code == expectedCode);
    }

    [Fact]
    public void Validation_RejectsSkuFactsPlacedInCommonAttributes()
    {
        var request = Fixture.Request();
        var response = Fixture.Response(request);
        response = response with { ProductMappings = [.. response.ProductMappings, response.Variants[0].Mappings[0]] };
        Assert.Contains(ProductMappingValidator.Validate(request, response).Issues, i => i.Code == "evidence.wrong_sku");
    }

    [Fact]
    public void Validation_RequiresEveryRealSkuAndRejectsInventedCombinations()
    {
        var request = Fixture.Request();
        var response = Fixture.Response(request);
        response = response with { Variants = [response.Variants[0], new("sku:blue-S", [])] };
        var result = ProductMappingValidator.Validate(request, response);
        Assert.Contains(result.Issues, i => i.Code == "sku.omitted");
        Assert.Contains(result.Issues, i => i.Code == "sku.fabricated");
    }

    [Fact]
    public void Validation_ExplicitMissingRequiredIsUnresolvedNotInventedButOmissionIsAnError()
    {
        var request = Fixture.Request();
        var response = Fixture.Response(request);
        response = response with { ProductMappings = [new(10, null, "missing_evidence", [], [], "没有材质证据。") ] };
        var missing = ProductMappingValidator.Validate(request, response);
        Assert.True(missing.ContractValid);
        Assert.Equal(3, missing.UnresolvedRequiredCount);
        var omitted = ProductMappingValidator.Validate(request, response with { ProductMappings = [] });
        Assert.False(omitted.ContractValid);
        Assert.Equal(3, omitted.Issues.Count(i => i.Code == "required.omitted"));
    }

    [Theory]
    [InlineData("Integer", "12.5")]
    [InlineData("Number", "200g")]
    [InlineData("Number", "NaN")]
    [InlineData("Boolean", "yes")]
    [InlineData("UnknownType", "anything")]
    public void Validation_ChecksScalarTypesAndNeverPretendsUnsupportedTypesPassed(string type, string text)
    {
        var request = Fixture.Request();
        request = request with { Attributes = request.Attributes.Select(a => a.AttributeId == 10 ? a with { Type = type } : a).ToArray() };
        var response = Fixture.Response(request);
        response = response with { ProductMappings = [response.ProductMappings[0] with { Values = [new(text, null)] }] };
        Assert.Contains(ProductMappingValidator.Validate(request, response).Issues, i => i.Code == "value.type");
    }

    [Fact]
    public void Validation_ComplexGroupMustHaveInstanceAndRequiredMembers()
    {
        var request = Fixture.Request();
        request = request with
        {
            Attributes = [.. request.Attributes,
                new(40, 900, "部件", "", "String", false, true, 1, 0, []),
                new(41, 900, "数量", "", "Integer", false, true, 1, 0, [])],
        };
        var response = Fixture.Response(request);
        var evidence = new[] { "product:f001" };
        response = response with { ProductMappings = [.. response.ProductMappings,
            new(40, "part-1", "suggested", [new("item", null)], evidence, "源部件。"),
            new(41, "part-2", "suggested", [new("1", null)], evidence, "源数量。") ] };
        var incomplete = ProductMappingValidator.Validate(request, response);
        Assert.Contains(incomplete.Issues, i => i.Code == "complex.required_member");
        Assert.Equal(6, incomplete.UnresolvedRequiredCount); // Two incomplete required members on each of three SKUs.
        response = response with { ProductMappings = response.ProductMappings.Select(m => m.AttributeId == 40
            ? m with { ComplexInstanceKey = null } : m).ToArray() };
        Assert.Contains(ProductMappingValidator.Validate(request, response).Issues, i => i.Code == "complex.missing");
    }

    [Fact]
    public void Validation_SkuOverridePreservesOtherComplexInstances()
    {
        var request = Fixture.Request();
        request = request with { Attributes = [.. request.Attributes,
            new(40, 900, "部件", "", "String", false, true, 1, 0, []),
            new(41, 900, "数量", "", "Integer", false, true, 1, 0, [])] };
        var response = Fixture.Response(request);
        response = response with { ProductMappings = [.. response.ProductMappings,
            new(40, "part-1", "suggested", [new("first", null)], ["product:f001"], "第一个部件。"),
            new(41, "part-1", "suggested", [new("1", null)], ["product:f001"], "第一个数量。"),
            new(40, "part-2", "suggested", [new("second", null)], ["product:f001"], "第二个部件。"),
            new(41, "part-2", "suggested", [new("2", null)], ["product:f001"], "第二个数量。")],
            Variants = [response.Variants[0] with { Mappings = [.. response.Variants[0].Mappings,
                new(41, "part-1", "suggested", [new("3", null)], [request.Skus[0].FactIds[0]], "当前 SKU 的数量。") ] },
                .. response.Variants.Skip(1)] };
        var result = ProductMappingValidator.Validate(request, response);
        Assert.True(result.ContractValid);
        Assert.Equal(0, result.UnresolvedRequiredCount);
        var quantities = result.Rows.Where(r => r.ScopeKey == request.Skus[0].VariantKey && r.AttributeId == 41).ToArray();
        Assert.Equal(2, quantities.Length);
        Assert.Contains(quantities, r => r.AttributeName.EndsWith("[part-1]") && r.ValueDisplay == "3");
        Assert.Contains(quantities, r => r.AttributeName.EndsWith("[part-2]") && r.ValueDisplay == "2");
    }

    [Fact]
    public void Validation_NullCollectionsAndNestedNullsAreReportedWithoutCrashing()
    {
        var request = Fixture.Request();
        var response = Fixture.Response(request);
        Assert.False(ProductMappingValidator.Validate(request, response with { ProductMappings = null! }).ContractValid);
        Assert.False(ProductMappingValidator.Validate(request, response with { Variants = [null!] }).ContractValid);
        var variant = response.Variants[0];
        Assert.False(ProductMappingValidator.Validate(request, response with
            { Variants = [variant with { Mappings = [null!] }, .. response.Variants.Skip(1)] }).ContractValid);
        response = response with { ProductMappings = [response.ProductMappings[0] with { Values = null! }] };
        Assert.False(ProductMappingValidator.Validate(request, response).ContractValid);
    }

    [Fact]
    public async Task Runner_UsesAiForAllTargetsQueriesDictionaryByEvidenceAndMakesAtMostTwoPasses()
    {
        var input = ProductMappingInputBuilder.Create(Fixture.Input(), "test");
        var mapper = new Fixture.Mapper(request => Fixture.Response(request, request.Attributes.Single(a => a.AttributeId == 20).DictionaryCandidates.Count == 0));
        var dictionaries = new Fixture.Dictionary();
        var result = await new ProductMappingRunner(mapper, dictionaries).RunAsync(input,
            new("test-key"), new("client", "ozon-key"), null, CancellationToken.None);
        Assert.Equal(2, mapper.Requests.Count);
        Assert.Equal(2, dictionaries.Queries.Count); // Red appears on two SKUs, but is queried once.
        Assert.All(mapper.Requests, r => Assert.Contains(r.Attributes, a => a.AttributeId == 30));
        Assert.True(result.Validation.ContractValid);
        Assert.Equal(0, result.Validation.UnresolvedRequiredCount);
        Assert.Equal(input.Request.InputFingerprint, result.Request.InputFingerprint);
        Assert.Equal(6, result.TotalTokens);
        Assert.False(result.ReadyForListing);
    }

    [Theory]
    [InlineData(false, "dictionary.no_candidates")]
    [InlineData(true, "dictionary.query_failed")]
    public async Task Runner_DictionaryAbsenceAndFailureDoNotBecomeMissingSourceFacts(bool fail, string code)
    {
        var input = ProductMappingInputBuilder.Create(Fixture.Input(), "test");
        var mapper = new Fixture.Mapper(request => Fixture.Response(request, true));
        var result = await new ProductMappingRunner(mapper, new Fixture.Dictionary { Fail = fail, Empty = true })
            .RunAsync(input, new("test-key"), new("client", "ozon-key"), null, CancellationToken.None);
        Assert.Single(mapper.Requests);
        Assert.Contains(result.Validation.Issues, i => i.Code == code);
        Assert.All(result.Response!.Variants, v => Assert.Equal("dictionary_pending", v.Mappings[0].Status));
        Assert.Equal(3, result.Validation.UnresolvedRequiredCount);
    }

    [Fact]
    public async Task Runner_RefinementFailureKeepsValidatedFirstPassAndDoesNotSelectDictionaryValues()
    {
        var input = ProductMappingInputBuilder.Create(Fixture.Input(), "test");
        var mapper = new Fixture.Mapper(request => request.Attributes.Any(a => a.DictionaryCandidates.Count > 0)
            ? throw new HttpRequestException("private-provider-detail") : Fixture.Response(request, true));
        var result = await new ProductMappingRunner(mapper, new Fixture.Dictionary())
            .RunAsync(input, new("test-key"), new("client", "ozon-key"), null, CancellationToken.None);
        Assert.Equal(2, mapper.Requests.Count);
        Assert.Single(result.Calls);
        Assert.True(result.Validation.ContractValid);
        Assert.Equal(3, result.Validation.UnresolvedRequiredCount);
        Assert.All(result.Response!.Variants, v =>
        {
            Assert.Equal("dictionary_pending", v.Mappings[0].Status);
            Assert.All(v.Mappings[0].Values, value => Assert.Null(value.DictionaryValueId));
        });
        Assert.Contains(result.Validation.Issues, i => i.Code == "ai.refinement_failed");
        Assert.DoesNotContain(result.Validation.Issues, i => i.Message.Contains("private-provider-detail"));
        Assert.False(result.ReadyForListing);
    }

    [Fact]
    public async Task Runner_InvalidFirstPassCannotTriggerDictionaryQueriesOrASecondModelCall()
    {
        var input = ProductMappingInputBuilder.Create(Fixture.Input(), "test");
        var mapper = new Fixture.Mapper(request => Fixture.Response(request, true) with { RequestId = "other-product" });
        var dictionary = new Fixture.Dictionary();
        var result = await new ProductMappingRunner(mapper, dictionary)
            .RunAsync(input, new("test-key"), new("client", "ozon-key"), null, CancellationToken.None);
        Assert.False(result.Validation.ContractValid);
        Assert.Single(mapper.Requests);
        Assert.Empty(dictionary.Queries);
    }

    [Fact]
    public async Task Runner_CancellationStopsBeforeAnyDictionaryWork()
    {
        using var cancellation = new CancellationTokenSource();
        var input = ProductMappingInputBuilder.Create(Fixture.Input());
        var mapper = new Fixture.Mapper(request => { cancellation.Cancel(); return Fixture.Response(request, true); });
        var dictionary = new Fixture.Dictionary();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProductMappingRunner(mapper, dictionary)
            .RunAsync(input, new("test-key"), new("client", "ozon-key"), null, cancellation.Token));
        Assert.Empty(dictionary.Queries);
    }

    internal static class Fixture
    {
        public static FieldMatchingInput Input() => new("1.0", "job", "collection",
            new(0, 1, "source-offer", "https://detail.1688.com/offer/source-offer.html", "2026-09-20T00:00:00Z", "success"),
            new("1688", "zh-CN", "商品", [new("f001", "attribute", "材质", "涤纶", "detail", "$.facts[0]"),
                    new("f002", "attribute", "风格", "日常", "detail", "$.facts[1]")],
                [new("m001", "image", "SHOULD_NOT_BE_SENT", 1, "$.images[0]")],
                [new("p001", "SHOULD_NOT_BE_SENT", "$.price")], [], [],
                [Sku("red-S", "source-1", "红色", "S", 10), Sku("red-M", "source-2", "红色", "M", 10),
                    Sku("blue-M", "source-3", "蓝色", "M", 0)], "verified"),
            new("Ozon", 100, 200, "测试类型", true,
                [new(10, 0, "材质", "", "String", "", false, true, 1, 0),
                    new(20, 0, "颜色", "", "String", "", false, true, 1, 300),
                    new(30, 0, "风格", "", "String", "", false, false, 1, 0)]));

        private static FieldMatchingSkuCombination Sku(string key, string id, string color, string size, long stock) =>
            new(key, "structured-json", new Dictionary<string, string?> { ["颜色"] = color, ["尺码"] = size }, 10m, stock)
            { SkuId = id };

        public static ProductMappingRequest Request()
        {
            var request = ProductMappingInputBuilder.Create(Input(), "test").Request;
            return request with { Attributes = request.Attributes.Select(a => a.AttributeId == 20
                ? a with { DictionaryCandidates = [new(101, "красный"), new(102, "синий")] } : a).ToArray() };
        }

        public static ProductMappingResponse Response(ProductMappingRequest request, bool pending = false) =>
            new(request.RequestId,
                [new(10, null, "suggested", [new("полиэстер", null)], ["product:f001"], "原始材质为涤纶。"),
                    new(30, null, "suggested", [new("повседневный", null)], ["product:f002"], "源风格为日常。")],
                request.Skus.Select(s => new ProductVariantSuggestion(s.VariantKey,
                    [new(20, null, pending ? "dictionary_pending" : "suggested",
                        [new(s.Options["颜色"] == "红色" ? "красный" : "синий",
                            pending ? null : s.Options["颜色"] == "红色" ? 101 : 102)],
                        [request.Facts.Single(f => f.ScopeKey == s.VariantKey && f.Label == "颜色").FactId], "使用当前 SKU 的颜色证据。")])).ToArray(), []);

        public sealed class Mapper(Func<ProductMappingRequest, ProductMappingResponse> map) : IProductSemanticMapper
        {
            public List<ProductMappingRequest> Requests { get; } = [];
            public Task<ProductMappingCallResult> MapAsync(QwenApiCredentials credentials, ProductMappingRequest request,
                CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(new ProductMappingCallResult("provider-test", "test-model", "test-prompt", "{}",
                    new(1, 2, 3), map(request), []));
            }
        }

        public sealed class Dictionary : IOzonDictionaryService
        {
            public bool Fail { get; init; }
            public bool Empty { get; init; }
            public List<string> Queries { get; } = [];
            public Task<IReadOnlyList<OzonDictionaryValue>> SearchAttributeValuesAsync(OzonTemporaryCredentials credentials,
                long category, long type, long attribute, string value, CancellationToken token)
            {
                Queries.Add(value);
                Assert.Equal(100, category); Assert.Equal(200, type); Assert.Equal(20, attribute);
                if (Fail) throw new HttpRequestException("sensitive-provider-details");
                IReadOnlyList<OzonDictionaryValue> result = Empty ? [] :
                    [new(value == "красный" ? 101 : 102, value, "", "SHOULD_NOT_BE_SENT")];
                return Task.FromResult(result);
            }
            public Task<IReadOnlyList<OzonDictionaryValue>> GetAttributeValuesAsync(OzonTemporaryCredentials credentials,
                long category, long type, long attribute, string language, CancellationToken token) =>
                throw new InvalidOperationException("Phase 1 does not download entire dictionaries.");
        }
    }
}
