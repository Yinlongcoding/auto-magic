using System.IO;
using System.Text.Json;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Infrastructure.Ozon.Mapping;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "AutoMagic-rule-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition,string description)
{ if (!condition) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
var repositoryStore = new JsonFieldMappingStore(Path.Combine(Directory.GetCurrentDirectory(), "rules"));
var configuredBundle = repositoryStore.Load(200000933,93182);
var configuredBindings = configuredBundle.Fields.Select(f => f.Module!.Binding).ToArray();
var originBinding = configuredBindings.Single(b => b.Id == 4389);
FieldBindingRow Origin(params (string Label, string Value)[] facts)
{
    var input = Input("origin-check",200);
    input = input with {
        Source = input.Source with { Facts = facts.Select((f,i) =>
            new FieldMatchingSourceFact("origin"+i, "attribute", f.Label, f.Value, "detail", "$.facts["+i+"]")).ToArray() },
        Target = input.Target with { Attributes = [new(4389,0,"原产国","","String","",false,true,1,123)] }
    };
    return FieldBindingPreview.Create(input, [originBinding]).Single();
}
Check(new[] { "广州", "深圳", "东莞", "汕头", "杭州" }.All(city =>
    Origin(("产地", city + "市")).InputText == "中国"), "Configured origin regions match substrings in city names");
Check(Origin(("产地","广州广东")).InputText == "中国" &&
      Origin(("产地","广州广东")).Resolution == "condition",
    "Origin contains matching accepts the configured city inside a longer source value");
var fullOrigin = Origin(("原产国","广东省广州市白云区"));
Check(fullOrigin.InputText == "中国" && fullOrigin.Resolution == "condition" &&
      fullOrigin.SourceValues == "广东省广州市白云区" && !fullOrigin.Reviewed,
    "Origin substring conversion preserves evidence and requires confirmation");
Check(Origin().InputText == "中国" && Origin().Resolution == "fallback" && Origin().FactIds.Length == 0,
    "Missing origin uses explicit China fallback without fabricating evidence");
Check(Origin(("产地","浙江")).InputText == "中国" && Origin(("产地","浙江")).Resolution == "fallback" &&
      Origin(("原产国","日本")).InputText == "中国",
    "Unlisted nonempty origins use the configured China fallback");
Check(Origin(("原产国","日本"),("产地","广州")).InputText == "中国" &&
      Origin(("产地","广州"),("产地","深圳")).InputText == "中国",
    "Competing origin facts use the configured China fallback");
Check(Origin(("备注","广州")).Resolution == "fallback",
    "Origin conditions do not match unrelated source labels");
var conditionRule = new FieldBinding(20, "颜色", Conditions: [new("颜色", "equals", "酒红色", "条件结果")], FallbackValue: "兜底结果");
var conditionHit = FieldBindingPreview.Create(Input("condition-hit",200), [conditionRule]).Single();
Check(conditionHit.InputText == "条件结果" && conditionHit.Resolution == "condition" && !conditionHit.Reviewed,
    "Exact condition hit produces a reviewable result");
var conditionMiss = FieldBindingPreview.Create(Input("condition-miss",200),
    [conditionRule with { Conditions = [new("颜色", "equals", "无袖", "无袖连衣裙")] }]).Single();
var conditionMissing = FieldBindingPreview.Create(Input("condition-missing",200),
    [conditionRule with { Conditions = [new("袖型", "equals", "无袖", "无袖连衣裙")] }]).Single();
Check(conditionMiss.InputText == "兜底结果" && conditionMissing.InputText == "兜底结果" &&
      conditionMissing.Resolution == "fallback" && conditionMissing.UsedDefault,
    "Explicit condition fallback covers nonmatching and missing source facts");
var conditionInvalid = FieldBindingPreview.Create(Input("condition-invalid",200),
    [conditionRule with { Id = 999 }]).Single();
Check(conditionInvalid.InputText == "" && !conditionInvalid.SchemaValid,
    "Conditional fallback cannot bypass Schema validation");
try {
    FieldBindingPreview.Create(Input("condition-operator",200),
        [conditionRule with { Conditions = [new("颜色", "regex", "红", "红色")] }]);
    throw new Exception("Expected invalid operator rejection");
} catch (InvalidDataException) { Check(true, "Unknown condition operators are rejected"); }
var schemaCacheRoot = Path.Combine(root, "schema-cache");
var schemaCache = new AutoMagic.Infrastructure.Ozon.OzonSchemaCache(schemaCacheRoot);
var cachedSchema = new OzonCategorySchema(100, 200, DateTimeOffset.UtcNow,
    [new(20, 0, "颜色", "", "String", false, true, 300, 1, "")]);
schemaCache.Save(cachedSchema);
Check(new AutoMagic.Infrastructure.Ozon.OzonSchemaCache(schemaCacheRoot).Load(100,200)?.Attributes[0].DictionaryId == 300 &&
      schemaCache.Load(100,201) is null && schemaCache.Load(101,200) is null,
    "Schema cache survives restart and isolates category/type");
File.WriteAllText(Path.Combine(schemaCacheRoot,"100","200.zh-Hans.json"), "{}");
try { schemaCache.Load(100,200); throw new Exception("Expected invalid cache rejection"); }
catch (InvalidDataException) { Check(true, "Invalid Schema cache is rejected"); }
schemaCache.Save(cachedSchema);
Check(schemaCache.Load(100,200)?.CapturedAt == cachedSchema.CapturedAt, "Manual refresh replaces damaged cache and preserves capture time");
var defaults = FieldBindingPreview.Create(Input("defaults",200),
    [new(20, "颜色", ["颜色"], "默认颜色"), new(10, "材质", ["不存在"], "默认材质")]);
Check(defaults[0].InputText == "酒红色" && !defaults[0].UsedDefault &&
      defaults[1].InputText == "默认材质" && defaults[1].UsedDefault && !defaults[1].Reviewed,
    "Explicit defaults only fill missing sources and still require human review");
var defaultConflict = FieldBindingPreview.Create(Input("default-conflict",200),
    [new(20, "颜色", ["颜色", "材质"], "默认颜色")]);
Check(defaultConflict[0].InputText == "" && !defaultConflict[0].UsedDefault,
    "Defaults do not replace conflicting source values");
var previewRows = FieldBindingPreview.Create(Input("binding-preview",200),
    [new(20, "颜色", ["颜色"]), new(10, "材质", ["猜测"]), new(999, "无效字段", ["颜色"])]);
Check(previewRows[0].InputText == "酒红色" && previewRows[0].ScopeKeys.SequenceEqual(["product"]),
    "Field preview keeps one shared product value without dictionary resolution");
Check(previewRows[1].InputText == "" && !previewRows[2].SchemaValid && previewRows[2].InputText == "",
    "Field preview excludes uncertain facts and blocks attributes absent from Schema");
var requiredOnlyInput = Input("required-only",200) with {
    Target = Input("required-only",200).Target with { Attributes = [
        new(20,0,"颜色","","String","",false,true,1,300),
        new(10,0,"材质","","String","",false,false,1,0),
        new(8292,0,"合并至一张卡片","","String","",false,true,0,0)
    ] }
};
var requiredOnly = FieldBindingPreview.CreateRequired(requiredOnlyInput,
    [new(20,"颜色",["颜色"]), new(10,"材质",["材质"])]);
Check(requiredOnly.Count == 2 && requiredOnly.All(row => row.AttributeId != 10) &&
      requiredOnly.Single(row => row.AttributeId == 20).InputText == "酒红色",
    "Required-only preview excludes optional bindings");
var unconfiguredRequired = requiredOnly.Single(row => row.AttributeId == 8292);
Check(unconfiguredRequired.InputText == "" && unconfiguredRequired.SchemaValid &&
      unconfiguredRequired.Resolution == "unresolved" && unconfiguredRequired.Status.Contains("未配置映射"),
    "Unconfigured required attributes remain visible and blank for human input");
var cardBinding = configuredBindings.Single(b => b.Id == 8292);
var cardInput = Input("925890695648",93182) with {
    Target = requiredOnlyInput.Target with { DescriptionCategoryId = 200000933, TypeId = 93182 },
    Source = Input("925890695648",93182).Source with { SkuCombinations = [
        new("white-s", "verified", new Dictionary<string,string?> { ["颜色"] = "白色", ["尺码"] = "S" }, null, null),
        new("red-m", "dimension-cartesian", new Dictionary<string,string?> { ["颜色"] = "红色", ["尺码"] = "M" }, null, null)
    ] }
};
var cardRow = FieldBindingPreview.CreateRequired(cardInput, configuredBindings).Single(r => r.AttributeId == 8292);
var cardRequest = ProductMappingInputBuilder.Create(cardInput).Request;
Check(cardRow.InputText == "1688-925890695648" && cardRow.ScopeKeys.SequenceEqual(["product"]) &&
      cardRow.Resolution == "generated" && !cardRow.Reviewed && !cardRow.UsedDefault &&
      cardRow.FactIds.Length == 0 && cardRow.SourceValues.Contains("925890695648") &&
      cardRequest.Skus.Count == 2 && cardRequest.Skus.Select(s => s.MerchantSku).Distinct().Count() == 2,
    "Configured card grouping shares one reviewable product value across distinct SKU identities");
var recapturedCard = cardInput with {
    CollectionId = "another-collection",
    ProductRef = cardInput.ProductRef with { CapturedAt = "2026-10-08" },
    Source = cardInput.Source with { Title = "改名后的商品", SkuCombinations = cardInput.Source.SkuCombinations.Reverse().ToArray() }
};
Check(FieldBindingPreview.Create(recapturedCard, [cardBinding]).Single().InputText == cardRow.InputText &&
      FieldBindingPreview.Create(cardInput with { Source = cardInput.Source with { SkuCombinations = [] } },
          [cardBinding]).Single().InputText == cardRow.InputText,
    "Card key survives recapture, title changes, SKU order changes and absence of variants");
Check(FieldBindingPreview.Create(cardInput with {
          ProductRef = cardInput.ProductRef with { OfferId = "925890695649" }
      }, [cardBinding]).Single().InputText != cardRow.InputText,
    "Different source products never share the generated card key");
foreach (var cardAttribute in new FieldMatchingTargetAttribute[] {
    new(8292,1,"合并至一张卡片","","String","",false,true,1,0),
    new(8292,0,"合并至一张卡片","","String","",true,true,1,0),
    new(8292,0,"合并至一张卡片","","String","",false,true,1,123),
    new(8292,0,"合并至一张卡片","","Integer","",false,true,1,0)
})
{
    var invalidCard = FieldBindingPreview.Create(cardInput with {
        Target = cardInput.Target with { Attributes = [cardAttribute] }
    }, [cardBinding]).Single();
    Check(invalidCard.InputText == "" && !invalidCard.SchemaValid,
        "Card generation rejects incompatible Schema: " + JsonSerializer.Serialize(cardAttribute));
}
Check(FieldBindingPreview.Create(cardInput with {
          Source = cardInput.Source with { Platform = "other" }
      }, [cardBinding]).Single().InputText == "",
    "Card generation leaves unsupported source platforms unresolved");
try {
    FieldBindingPreview.Create(cardInput with { ProductRef = cardInput.ProductRef with { OfferId = "" } }, [cardBinding]);
    throw new Exception("Expected missing source identity rejection");
} catch (ArgumentException) { Check(true, "Missing source identity cannot generate a shared card key"); }
foreach (var invalidGenerator in new[] {
    cardBinding with { Generator = "unknown" }, cardBinding with { Id = 20 },
    cardBinding with { Match = ["颜色"] }, cardBinding with { DefaultValue = "固定组" },
    cardBinding with { Conditions = [new("颜色", "equals", "红色", "组")] },
    cardBinding with { FallbackValue = "固定组" }
})
{
    try {
        FieldBindingPreview.Create(cardInput, [invalidGenerator]);
        throw new Exception("Expected invalid card generator rejection");
    } catch (InvalidDataException) { Check(true, "Invalid or competing card generator definitions are rejected"); }
}
var conflictPreview = FieldBindingPreview.Create(Input("binding-conflict",200), [new(20, "颜色", ["颜色", "材质"])]);
Check(conflictPreview.Single().InputText == "" && conflictPreview.Single().SourceValues.Contains("棉"),
    "Multiple distinct source values remain visible without selecting an answer");
try {
    FieldBindingPreview.Create(Input("duplicate-binding",200), [new(20, "颜色", ["颜色"]), new(20, "颜色", ["材质"])]);
    throw new Exception("Expected duplicate binding rejection");
} catch (InvalidDataException) { Check(true, "Duplicate target bindings are rejected"); }

ExplicitModuleChecks.Run(Input("explicit-modules",200), Check);
ReferenceChainChecks.Run(Input("reference-chains",200), Check);
var uiStore = ExplicitModuleChecks.CreateFixture(Path.Combine(root,"ui-rules"));
UiCheck.Run(new FieldMappingService(uiStore),root,Input("ui-confirmed-values",200));
Check(true,"WPF explicit module form and command construct, bind and render");
Console.WriteLine($"{checks} integration checks passed. Temporary evidence: {root}");
static FieldMatchingInput Input(string offer,long type)=>new("1","job","batch",new(0,1,offer,"https://detail.1688.com/offer/"+offer,"2026-09-28","success"),
    new("1688","zh-CN","测试商品",
        [new("color","attribute","颜色","酒红色","detail","$.color"),new("material","attribute","材质","棉","detail","$.material"),
         new("guess","derived","猜测","女","inference:title","$.title")],[],[],[],[],[],"verified"),
    new("Ozon",100,type,"测试",true,[new(20,0,"颜色","","String","",false,true,1,300),new(10,0,"材质","","String","",false,false,1,0)]));
