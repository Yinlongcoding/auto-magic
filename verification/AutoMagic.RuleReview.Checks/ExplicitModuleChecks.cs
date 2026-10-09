using System.IO;
using System.Text.Json;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Infrastructure.Ozon.Mapping;
using Microsoft.Data.Sqlite;

static class ExplicitModuleChecks
{
    private const string ColorPath = "common/apparel/color.json";
    private static readonly DictionaryValueMapping Burgundy = new("burgundy",1,"active","酒红色","Бордовый",101);
    private static readonly FieldRuleModule ColorModule = new(1,"common/apparel/color",1,"active",null,null,300,
        new(20,"颜色",["颜色"]),[Burgundy]);
    private static string PlanPath(string root) => Path.Combine(root,"categories","100","types","200.json");
    private static void Write<T>(string path,T value)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,JsonSerializer.Serialize(value,JsonFieldMappingStore.Json)); }
    public static JsonFieldMappingStore CreateFixture(string root)
    {
        Write(PlanPath(root),new TypeFieldPlan(2,100,200,[new(20,ColorPath)]));
        Write(Path.Combine(root,ColorPath),ColorModule);
        return new(root);
    }
    public static void Run(FieldMatchingInput source,Action<bool,string> check)
    {
        var root = Path.Combine(Path.GetTempPath(),"AutoMagic-explicit-modules-"+Guid.NewGuid().ToString("N"));
        var store = CreateFixture(root); var service = new FieldMappingService(store);
        void Module(FieldRuleModule module) => Write(Path.Combine(root,ColorPath),module);
        void Plan(params FieldModuleReference[] fields) => Write(PlanPath(root),new TypeFieldPlan(2,100,200,fields));
        FieldBindingRow Confirm(string? text = null) {
            var row = service.CreateRequired(source).Single();
            if (text is not null) row.InputText = text;
            row.Reviewed = true; return row;
        }
        var row = Confirm(); service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId == 101 && row.TargetValue == "Бордовый" && row.ModulePath == ColorPath,
            "A complete type reference drives both source binding and Chinese value matching");
        foreach (var text in new[] { "未知颜色","酒红","" }) {
            var unresolved = Confirm(text); service.ResolveConfirmedFields(source,[unresolved]);
            check(unresolved.ValueId is null && unresolved.TargetValue is null,"Unmatched/approximate/empty Chinese stays blank: "+text);
        }
        var unconfirmed = service.CreateRequired(source).Single(); service.ResolveConfirmedFields(source,[unconfirmed]);
        check(unconfirmed.ValueId is null,"Unconfirmed values never execute dictionary matching");
        row.InputText = "红色";
        check(!row.Reviewed && row.ValueId is null && row.TargetValue is null,"Editing text invalidates confirmation and old matches");
        row = Confirm(); service.ResolveConfirmedFields(source,[row]); row.Reviewed = false;
        check(row.ValueId is null,"Unchecking confirmation clears the old valueId");
        row = Confirm(); Module(ColorModule with { Values = [Burgundy with { Revision=2,ValueId=102,TargetValue="更新值" }] });
        service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId == 102 && row.Reviewed,"Value-only module edits take effect without regenerating source bindings");
        Module(ColorModule with { Binding = new(20,"颜色",["其他颜色"]) });
        service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null && !row.Reviewed && row.ValueStatus.Contains("重新生成"),"Changed module bindings reject stale human confirmations");
        Module(ColorModule); row=Confirm();
        Write(Path.Combine(root,"dictionary","special-color.json"),ColorModule with { ModuleId="dictionary/special-color" });
        Plan(new FieldModuleReference(20,"dictionary/special-color.json")); service.ResolveConfirmedFields(source,[row]);
        check(!row.Reviewed && row.ValueId is null,"Changing the type reference requires regeneration even when values coincide");
        Plan(new FieldModuleReference(20,ColorPath));
        Module(ColorModule with { Values = [Burgundy,Burgundy with { MappingId="ambiguous",ValueId=102,TargetValue="Красный" }] });
        row=Confirm(); service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null && row.ValueStatus.Contains("多个"),"Ambiguous Chinese values in the referenced module remain blank");
        Module(ColorModule with { Values = [Burgundy with { Status="candidate" }] });
        row=Confirm(); service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null,"Candidate mappings cannot resolve confirmed Chinese");
        Module(ColorModule with { Status="disabled" }); row=Confirm(); service.ResolveConfirmedFields(source,[row]);
        check(row.InputText == "" && row.ValueId is null,"Disabled modules do not provide bindings or values");
        Module(ColorModule with { DescriptionCategoryId=100,TypeId=201 }); row=Confirm("酒红色");
        service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null && row.ValueStatus.Contains("不适用"),"Type-restricted modules cannot leak into another type");
        Module(ColorModule with { DictionaryId=301 }); row=Confirm("酒红色"); service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null,"Referenced dictionaries must match the current target Schema");
        Module(ColorModule); row=Confirm();
        Plan(new FieldModuleReference(20,"dictionary/missing.json")); row=Confirm("酒红色"); service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null && row.ValueStatus.Contains("不存在"),"Missing modules leave the field blank without implicit common fallback");
        Plan(); row=Confirm("酒红色"); service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null && row.ModulePath is null,"Unreferenced common modules are never applied");
        Plan(new FieldModuleReference(20,ColorPath));
        File.WriteAllText(Path.Combine(root,"common","unreferenced.json"),"invalid JSON");
        row=Confirm(); service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId == 101,"Broken unreferenced modules are not scanned");
        var unknownType = source with { Target=source.Target with { TypeId=201 } };
        var unknownRow=service.CreateRequired(unknownType).Single(); unknownRow.InputText="酒红色";unknownRow.Reviewed=true;
        service.ResolveConfirmedFields(unknownType,[unknownRow]);
        check(unknownRow.ValueId is null,"A type without a plan cannot inherit a global module");
        foreach (var bad in new[] { "../common/apparel/color.json","C:/color.json","common/../color.json","reference/color.json" }) {
            Plan(new FieldModuleReference(20,bad));
            try { store.Load(100,200); throw new Exception("Expected path rejection"); }
            catch (InvalidDataException) { check(true,"Invalid module path is rejected: "+bad); }
        }
        Plan(new FieldModuleReference(20,ColorPath),new(20,ColorPath));
        try { store.Load(100,200); throw new Exception("Expected duplicate field rejection"); }
        catch (InvalidDataException) { check(true,"Duplicate attributes in a type plan are rejected"); }
        Plan(new FieldModuleReference(20,ColorPath)); Module(ColorModule with { ModuleId="wrong" });
        try { store.Load(100,200); throw new Exception("Expected module identity rejection"); }
        catch (InvalidDataException) { check(true,"Module identity must match its referenced path"); }
        Module(ColorModule with { Values=[Burgundy with { ValueId=0 }] });
        try { store.Load(100,200); throw new Exception("Expected invalid valueId rejection"); }
        catch (InvalidDataException) { check(true,"Nonpositive dictionary valueIds are rejected"); }
        Module(ColorModule); row=Confirm(); service.ResolveConfirmedFields(source,[row]);
        File.WriteAllText(Path.Combine(root,ColorPath),"{}");
        try { service.ResolveConfirmedFields(source,[row]); throw new Exception("Expected invalid JSON rejection"); }
        catch (JsonException) { check(row.ValueId is null,"Invalid referenced JSON clears stale results and fails explicitly"); }
        Module(ColorModule);
        var collection=source with { Target=source.Target with {
            Attributes=source.Target.Attributes.Select(a=>a.AttributeId==20?a with {IsCollection=true}:a).ToArray() },
            Source=source.Source with { SkuCombinations=[
                new("s","verified",new Dictionary<string,string?>{["颜色"]="酒红色",["尺码"]="S"},null,null),
                new("m","verified",new Dictionary<string,string?>{["颜色"]="酒红色",["尺码"]="M"},null,null)] } };
        var group=service.CreateRequired(collection).Single();group.Reviewed=true;service.ResolveConfirmedFields(collection,[group]);
        check(group.ValueId==101&&group.ScopeKeys.Length==2,"Shared SKU values resolve once for a collection-capable attribute");
        var direct=source with { Target=source.Target with {Attributes=[new(8292,0,"合并至一张卡片","","String","",false,true,0,0)]} };
        Write(Path.Combine(root,"common/product/card.json"),new FieldRuleModule(1,"common/product/card",1,"active",null,null,0,
            new(8292,"合并至一张卡片",Generator:"source-product-card-key"),[]));
        Plan(new FieldModuleReference(8292,"common/product/card.json"));
        var directRow=service.CreateRequired(direct).Single();directRow.Reviewed=true;service.ResolveConfirmedFields(direct,[directRow]);
        check(directRow.TargetValue=="1688-"+source.ProductRef.OfferId&&directRow.ValueId is null,
            "Explicit ordinary-text modules preserve product grouping without inventing valueIds");
        Plan(new FieldModuleReference(20,ColorPath)); row=Confirm();var request=ProductMappingInputBuilder.Create(source).Request;var field=store.Load(100,200).Fields.Single();
        ConfirmedFieldValueResolver.Resolve(request,row,field,_=>new(100,97));
        check(row.ValueId is null,"Low-confidence module audits block matching");
        ConfirmedFieldValueResolver.Resolve(request,row,field,key=>key.Contains("/burgundy/")?new(100,97):new(0,0));
        check(row.ValueId is null,"Low-confidence value audits block matching");
        using (var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"reviews.db")}.ToString())) {
            db.Open();using var command=db.CreateCommand();command.CommandText="""
                INSERT INTO drafts VALUES('legacy-draft','legacy data');
                INSERT INTO history(category_id,type_id,before_json,after_json,at) VALUES(100,200,'before','after','date');
                INSERT INTO audits VALUES('legacy-key','sample',0,'before','after','human','date');
                """;command.ExecuteNonQuery();
        }
        check(new JsonFieldMappingStore(root).Counts("legacy-key")==new ReviewCount(1,0),"Existing review statistics survive the new loader");
        var beforePlan=File.ReadAllText(PlanPath(root));var beforeModule=File.ReadAllText(Path.Combine(root,ColorPath));
        service.ResolveConfirmedFields(source,[Confirm()]);
        check(File.ReadAllText(PlanPath(root))==beforePlan&&File.ReadAllText(Path.Combine(root,ColorPath))==beforeModule,
            "Matching does not rewrite type plans or modules");
        using (var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"reviews.db")}.ToString())) {
            db.Open();using var command=db.CreateCommand();command.CommandText="SELECT COUNT(*) FROM drafts WHERE json='legacy data'";
            check(Convert.ToInt64(command.ExecuteScalar())==1,"Old drafts remain intact");
            command.CommandText="SELECT COUNT(*) FROM history WHERE after_json='after'";
            check(Convert.ToInt64(command.ExecuteScalar())==1,"Old rule history remains intact");
            command.CommandText="INSERT INTO pending VALUES(1,100,200,'old-hash','old-rule')";command.ExecuteNonQuery();
            try { store.Load(100,200);throw new Exception("Expected pending migration guard"); }
            catch (InvalidDataException) {
                command.CommandText="SELECT json FROM pending";
                check((string?)command.ExecuteScalar()=="old-rule"&&File.ReadAllText(PlanPath(root))==beforePlan,
                    "Legacy pending writes remain intact and cannot overwrite explicit plans");
            }
        }
        var repositoryStore=new JsonFieldMappingStore(Path.Combine(Directory.GetCurrentDirectory(),"rules"));
        var repository=repositoryStore.Load(200000933,93182);
        check(repository.Fields.Count==14&&repository.Fields.All(f=>f.Issue is null),"The migrated dress plan explicitly references all 14 maintained fields");
        var gender=repository.Fields.Single(f=>f.Reference.Id==9163).Module!;
        check(gender.Values.Select(v=>v.ValueId).SequenceEqual([22880,22881,22882,22883])&&gender.Binding.DefaultValue=="女性",
            "Migrated gender values and the existing dress default remain unchanged");
        var genderSource=source with { Target=source.Target with {
            DescriptionCategoryId=200000933,TypeId=93182,
            Attributes=[new(9163,0,"性别","","String","",false,true,0,320)] } };
        var genderService=new FieldMappingService(repositoryStore);
        var genderRow=genderService.CreateRequired(genderSource).Single();genderRow.Reviewed=true;
        genderService.ResolveConfirmedFields(genderSource,[genderRow]);
        check(genderRow.InputText=="女性"&&genderRow.ValueId==22881&&
            genderRow.ValueSourcePaths.SequenceEqual(["dictionary.320.93182","common.gender"]),
            "Actual dress gender default resolves through the dictionary-to-common reference chain");
        var style=repository.Fields.Single(f=>f.Reference.Id==23079).Module!;
        check(style.Values.Count==28&&style.Values.All(v=>v.DisplayZh is not ("T恤连衣裙" or "褶")),
            "All 28 verified dress style mappings survive migration without ambiguous entries");
        var snapshots=Path.Combine(Directory.GetCurrentDirectory(),"rules","reference","pre-explicit-modules");
        check(new[]{"93182.bindings.json","93182.values.json","gender.json"}.All(f=>File.Exists(Path.Combine(snapshots,f))),
            "Original binding and value rule snapshots remain available as reference data");
        using(var original=JsonDocument.Parse(File.ReadAllText(Path.Combine(snapshots,"93182.bindings.json")))) {
            var unchanged=original.RootElement.GetProperty("attributes").EnumerateArray().All(item=> {
                var binding=JsonSerializer.Deserialize<FieldBinding>(item.GetRawText(),ProductMappingJson.StrictOptions)!;
                var migrated=repository.Fields.Single(f=>f.Reference.Id==binding.Id).Module!.Binding;
                return JsonSerializer.Serialize(binding)==JsonSerializer.Serialize(migrated);
            });
            check(unchanged,"All original source bindings, conditions, defaults and fallbacks survive migration exactly");
        }
    }
}
