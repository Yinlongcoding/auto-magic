using System.IO;
using System.Text.Json;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Infrastructure.Ozon.Mapping;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "AutoMagic-rule-check-" + Guid.NewGuid().ToString("N"));
var store = new JsonRuleReviewStore(root);
var dictionary = new DictionaryStub();
var service = new RuleReviewService(store, dictionary);
var credentials = new OzonTemporaryCredentials("test", "test");
var token = CancellationToken.None;
var checks = 0;
void Check(bool condition, string description)
{ if (!condition) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
async Task<RuleReviewSession> Open(string offer, long type = 200) => await service.OpenAsync(Input(offer, type), credentials, token);
RuleReviewRow Color(RuleReviewSession s) => s.Rows.Single(r => r.Attribute.AttributeId == 20);

var session = await Open("first");
var originFile = JsonSerializer.Deserialize<FieldBindingFile>(File.ReadAllText(
    Path.Combine(Directory.GetCurrentDirectory(), "rules", "categories", "200000933", "types", "93182.bindings.json")),
    ProductMappingJson.StrictOptions)!;
var originBinding = originFile.Attributes.Single(b => b.Id == 4389);
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
var conflictPreview = FieldBindingPreview.Create(Input("binding-conflict",200), [new(20, "颜色", ["颜色", "材质"])]);
Check(conflictPreview.Single().InputText == "" && conflictPreview.Single().SourceValues.Contains("棉"),
    "Multiple distinct source values remain visible without selecting an answer");
try {
    FieldBindingPreview.Create(Input("duplicate-binding",200), [new(20, "颜色", ["颜色"]), new(20, "颜色", ["材质"])]);
    throw new Exception("Expected duplicate binding rejection");
} catch (InvalidDataException) { Check(true, "Duplicate target bindings are rejected"); }
Check(session.Rows.All(r => r.InputText == "" && r.SelectedCandidate is null), "Empty catalog leaves targets blank");
Check(session.Rows.All(r => r.Facts.All(f => f.Label != "猜测")), "Uncertain cleaned facts do not enter form evidence");
var color = Color(session); color.InputText = "酒红色"; color.Reviewed = true;
service.SaveDraft(session);
Check(!File.Exists(Path.Combine(root,"categories","100","types","200.json")), "Draft does not create a rule");
var reopened = await Open("first");
Check(Color(reopened).InputText == "酒红色" && !Color(reopened).Reviewed, "Draft restores text but not a new audit");
var ambiguous = await service.ConfirmAsync(session, credentials, token);
Check(color.SelectedCandidate is null && ambiguous.Unresolved == 2, "Chinese ambiguous search does not select a valueId");
Check(store.Load(100,200).EffectiveRules.Count == 0, "Unresolved values do not create rules");
session = service.RefreshBundle(session);
color.SelectedCandidate = color.Candidates.Single(c => c.ValueId == 101);
var saved = await service.ConfirmAsync(session, credentials, token);
Check(saved.Run.Validation.ContractValid && saved.SavedRules == 1, "Human dictionary selection validates and persists");
var file = store.Load(100,200).TypeFile;
Check(file.Rules.Single().Values.Single().ValueId == 101 && file.Rules.Single().Values.Single().DisplayZh == "酒红色", "JSON preserves Chinese input and official dictionary ID");
var second = await Open("second"); var secondColor = Color(second);
Check(secondColor.SelectedCandidate?.ValueId == 101 && secondColor.InputText == "酒红色", "Another product reuses learned rule");
Check(Color(await Open("third",201)).InputText == "", "Rule does not leak to another type");
secondColor.Reviewed = true;
await service.ConfirmAsync(second, credentials, token);
var key = secondColor.OriginalMappingKey!;
Check(store.Counts(key).Checked == 2, "Independent product audits accumulate");
second = service.RefreshBundle(second); secondColor.Reviewed = true;
await service.ConfirmAsync(second, credentials, token);
Check(store.Counts(key).Checked == 2, "Repeated confirmation is idempotent");
var correcting = await Open("fourth"); var correction = Color(correcting);
await service.SearchRowAsync(correcting, correction, credentials, token);
correction.SelectedCandidate = correction.Candidates.Single(c => c.ValueId == 102); correction.Reviewed = true;
await service.ConfirmAsync(correcting, credentials, token);
Check(store.Load(100,200).TypeFile.Rules.Single().Values.Single().Revision == 2, "Correction creates a new mapping revision");
Check(Color(await Open("fifth")).SelectedCandidate?.ValueId == 102, "Correction is reused immediately");
Check(store.Counts(key).Checked == 3 && store.Counts(key).Correct == 2, "Old version records the correction as an error");
var newerKey = correction.OriginalMappingKey!;
Check(store.Counts(newerKey).Checked == 1, "New revision does not inherit old score");

// At exactly 100 unique reviewed samples the threshold is inclusive.
var bundle = store.Load(100,200);
var audits = Enumerable.Range(1,99).Select(i => new RuleAudit(newerKey,"sample-"+i,i<=97,"before","after","tester")).ToArray();
store.Commit(new(100,200,bundle.Token,bundle.TypeFile,"threshold","{}",audits));
Check(store.Counts(newerKey).Trusted, "98/100 is trusted");
bundle=store.Load(100,200);
store.Commit(new(100,200,bundle.Token,bundle.TypeFile,"threshold","{}",[new(newerKey,"sample-100",false,"before","after","tester")]));
Check(store.Counts(newerKey).LowConfidence && Color(await Open("low-confidence")).InputText == "", "Below 98% with sufficient audits blanks target");

var stale = await Open("stale");
var typePath=Path.Combine(root,"categories","100","types","200.json");
File.AppendAllText(typePath," ");
try { await service.ConfirmAsync(stale,credentials,token); throw new Exception("Expected conflict"); }
catch(InvalidOperationException) { Check(true,"External rule changes reject stale saves"); }

var manual = await Open("manual"); var material=manual.Rows.Single(r=>r.Attribute.AttributeId==10);
material.SourceFactId=""; material.InputText="棉"; material.Reviewed=true;
await service.ConfirmAsync(manual,credentials,token);
Check(store.Load(100,200).EffectiveRules.All(r=>r.AttributeId!=10),"Manual fact without source cannot become a default rule");

var forged = await Open("forged",202); var forgedRow=Color(forged); forgedRow.InputText="酒红色"; forgedRow.Reviewed=true;
forgedRow.Candidates=[new(999,"made-up")]; forgedRow.CandidateQuery=forgedRow.InputText; forgedRow.SelectedCandidate=forgedRow.Candidates[0];
var rejected=await service.ConfirmAsync(forged,credentials,token);
Check(rejected.Unresolved==2 && store.Load(100,202).EffectiveRules.Count==0,"Forged IDs cannot be saved by manipulating form candidates");

// Replay a pending file write, simulating a process exit between DB commit and file replace.
var recoveryPath=Path.Combine(root,"categories","100","types","203.json");
var recoveryFile=new RuleFile(1,100,203,[]);
using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"reviews.db")}.ToString()))
{
    db.Open(); using var cmd=db.CreateCommand();
    cmd.CommandText="INSERT INTO pending VALUES(1,100,203,$h,$j)";
    cmd.Parameters.AddWithValue("$h",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])));
    cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(recoveryFile,JsonRuleReviewStore.Json)); cmd.ExecuteNonQuery();
}
store.Load(100,203);
Check(File.Exists(recoveryPath),"Interrupted file write is recovered from SQLite journal");
Check(File.ReadAllText(typePath).Contains("targetValue"),"Persisted rules are inspectable JSON");
// Category inheritance, type replacement, disable, and schema invalidation.
var learned=store.Load(100,200).TypeFile.Rules.Single();
var categoryRule=learned with { Action="add", Values=[learned.Values[0] with { ValueId=101, TargetValue="Бордовый" }] };
var categoryPath=Path.Combine(root,"categories","100","rules.json");
File.WriteAllText(categoryPath,JsonSerializer.Serialize(new RuleFile(1,100,null,[categoryRule]),JsonRuleReviewStore.Json));
Check(Color(await Open("inherited",201)).SelectedCandidate?.ValueId==101,"Category rule is inherited only without a type override");
Check(store.Load(100,200).EffectiveRules.Single().Values.Single().ValueId==102,"Type rule replaces category value mapping");
var disabledPath=Path.Combine(root,"categories","100","types","204.json");
File.WriteAllText(disabledPath,JsonSerializer.Serialize(new RuleFile(1,100,204,[categoryRule with { Action="disable" }]),JsonRuleReviewStore.Json));
Check(Color(await Open("disabled",204)).InputText=="","Explicit type disable suppresses inherited rule");
var changed=Input("schema-change",201);
changed=changed with {Target=changed.Target with {Attributes=changed.Target.Attributes.Select(a=>a.AttributeId==20?a with {DictionaryId=999}:a).ToArray()}};
Check(Color(await service.OpenAsync(changed,credentials,token)).InputText=="","Schema fingerprint change blocks automatic mapping");

var skuInput=Input("sku-conflict",205);
skuInput=skuInput with {Source=skuInput.Source with {SkuCombinations=[
    new("s", "verified",new Dictionary<string,string?>{{"尺码","S"}},null,null){SkuId="s"},
    new("m", "verified",new Dictionary<string,string?>{{"尺码","M"}},null,null){SkuId="m"}]}};
var skuSession=await service.OpenAsync(skuInput,credentials,token);
var groupedPreview = FieldBindingPreview.Create(skuInput, [new(20, "颜色", ["颜色"]), new(10, "材质", ["尺码"])]);
Check(groupedPreview.Count(r => r.AttributeId == 20) == 1 &&
      groupedPreview.Count(r => r.AttributeId == 10) == 2 &&
      groupedPreview.Where(r => r.AttributeId == 10).All(r => r.ScopeKeys.Length == 1),
    "Field preview shows product facts once and separates differing SKU values");
var skuColors=skuSession.Rows.Where(r=>r.Attribute.AttributeId==20).ToArray();
for(var i=0;i<skuColors.Length;i++)
{
    var row=skuColors[i]; row.InputText="酒红色"; row.Reviewed=true;
    await service.SearchRowAsync(skuSession,row,credentials,token);
    row.SelectedCandidate=row.Candidates.Single(c=>c.ValueId==(i==0?101:102));
}
try {await service.ConfirmAsync(skuSession,credentials,token);throw new Exception("Expected shared-value conflict");}
catch(InvalidOperationException){Check(true,"Conflicting SKU edits cannot overwrite one shared source rule");}
Check(!File.Exists(Path.Combine(root,"categories","100","types","205.json")),"Conflicting batch leaves files unchanged");

var cancelSession=await Open("cancel",206);Color(cancelSession).InputText="酒红色";Color(cancelSession).Reviewed=true;
using(var cancel=new CancellationTokenSource())
{
    cancel.Cancel();
    try{await service.ConfirmAsync(cancelSession,credentials,cancel.Token);throw new Exception("Expected cancellation");}
    catch(OperationCanceledException){Check(!File.Exists(Path.Combine(root,"categories","100","types","206.json")),"Cancellation does not commit rules");}
}
var persisted=JsonSerializer.Deserialize<ReviewDraft>(store.LoadDraft(session.DraftKey)!,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
Check(persisted.ConfirmedResult?.Response is not null && persisted.Rows.Any(r=>r.ValueId==101),"Confirmed product result and selected ID persist independently of rule learning");
Check(new JsonRuleReviewStore(root).Counts(newerKey).Checked==101,"Audit statistics survive store recreation");

var configuredRoot = Path.Combine(root, "configured-rules");
var configuredCommon = Path.Combine(configuredRoot, "common");
Directory.CreateDirectory(configuredCommon);
File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "rules", "common", "gender.json"),
    Path.Combine(configuredCommon, "gender.json"));
var configuredGender = new JsonRuleReviewStore(configuredRoot).Load(200000933, 93211).EffectiveRules.Single();
Check(configuredGender.AttributeId == 9163 && configuredGender.DictionaryId == 320 &&
      configuredGender.Values.Select(value => value.ValueId).SequenceEqual([22880, 22881, 22882, 22883]),
    "Configured common gender rule loads across category and type with all verified value IDs");
var dressRoot = Path.Combine(root, "dress-rules");
var dressTypeDirectory = Path.Combine(dressRoot, "categories", "200000933", "types");
Directory.CreateDirectory(dressTypeDirectory);
File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "rules", "categories", "200000933", "types", "93182.json"),
    Path.Combine(dressTypeDirectory, "93182.json"));
var dressRule = new JsonRuleReviewStore(dressRoot).Load(200000933, 93182).EffectiveRules.Single();
Check(dressRule.AttributeId == 23079 && dressRule.DictionaryId == 124413020 &&
      dressRule.Values.Count == 28 && dressRule.Values.Any(value => value.SourceValue == "阿巴亚" &&
          value.TargetValue == "абайя" && value.ValueId == 972087274),
    "Dress type rule loads 28 unambiguous mappings with the verified attribute and dictionary IDs");
Check(dressRule.Values.All(value => value.SourceValue is not ("T恤连衣裙" or "褶")),
    "Ambiguous dress style names remain unresolved instead of selecting an arbitrary value ID");
using (var countryCatalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
           "rules", "common", "catalogs", "origin-country.json"))))
{
    var rootElement = countryCatalog.RootElement;
    var countryValues = rootElement.GetProperty("values").EnumerateArray().ToArray();
    Check(rootElement.GetProperty("complete").GetBoolean() && countryValues.Length == 267 &&
          countryValues.Select(value => value.GetProperty("valueId").GetInt64()).Distinct().Count() == 267,
        "Complete common origin-country catalog contains 267 unique official value IDs");
    Check(countryValues.Any(value => value.GetProperty("valueId").GetInt64() == 90296 &&
                                     value.GetProperty("displayZh").GetString() == "中国" &&
                                     value.GetProperty("targetValue").GetString() == "Китай"),
        "Origin-country catalog maps China's official Chinese and Russian values by ID");
    Check(countryValues.GroupBy(value => value.GetProperty("displayZh").GetString())
                       .Count(group => group.Count() > 1) == 6,
        "Origin-country catalog preserves six ambiguous Chinese names for manual resolution");
}

var clearing=await Open("clear",201);var clearRow=Color(clearing);clearRow.InputText="";clearRow.Reviewed=true;
await service.ConfirmAsync(clearing,credentials,token);
var clearRevision=store.Load(100,201).TypeFile.Rules.Single().Values.Single().Revision;
clearing=service.RefreshBundle(clearing);
await service.ConfirmAsync(clearing,credentials,token);
Check(store.Load(100,201).TypeFile.Rules.Single().Values.Single().Revision==clearRevision,"Repeated clear does not generate extra revisions");
Check(Color(await Open("after-clear",201)).InputText=="","Clearing a learned value suspends it without a default blank mapping");

UiCheck.Run(service, session, root);
Check(true,"WPF form constructs, binds editable fields, and renders");
Console.WriteLine($"{checks} integration checks passed. Temporary evidence: {root}");

static FieldMatchingInput Input(string offer,long type)=>new("1","job","batch",new(0,1,offer,"https://detail.1688.com/offer/"+offer,"2026-09-28","success"),
    new("1688","zh-CN","测试商品",
        [new("color","attribute","颜色","酒红色","detail","$.color"),new("material","attribute","材质","棉","detail","$.material"),
         new("guess","derived","猜测","女","inference:title","$.title")],[],[],[],[],[],"verified"),
    new("Ozon",100,type,"测试",true,[new(20,0,"颜色","","String","",false,true,1,300),new(10,0,"材质","","String","",false,false,1,0)]));

sealed class DictionaryStub:IOzonDictionaryService
{
    public Task<IReadOnlyList<OzonDictionaryValue>> SearchAttributeValuesAsync(OzonTemporaryCredentials c,long category,long type,long attribute,string text,CancellationToken token)
    { token.ThrowIfCancellationRequested(); IReadOnlyList<OzonDictionaryValue> result = text switch
        { "酒红色" => [new(101,"Бордовый","",""),new(102,"Красный","","")],
          "Бордовый"=>[new(101,text,"","")],"Красный"=>[new(102,text,"","")],_=>[] };return Task.FromResult(result); }
    public Task<IReadOnlyList<OzonDictionaryValue>> GetAttributeValuesAsync(OzonTemporaryCredentials c,long category,long type,long attribute,string language,CancellationToken token)=>throw new NotSupportedException();
}
