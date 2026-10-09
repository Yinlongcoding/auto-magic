using System.IO;
using System.Text.Json;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Infrastructure.Ozon.Mapping;

static class ReferenceChainChecks
{
    public static void Run(FieldMatchingInput source,Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"AutoMagic-reference-chain-"+Guid.NewGuid().ToString("N"));
        var store=ExplicitModuleChecks.CreateFixture(root);var service=new FieldMappingService(store);
        var module=store.Load(100,200).Fields.Single().Module!;
        var value=module.Values.Single();
        void Write<T>(string path,T data) {
            var full=Path.Combine(root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full,JsonSerializer.Serialize(data,JsonFieldMappingStore.Json));
        }
        var terminal=new ValueDictionaryModule(1,"common/palette",1,"active",20,300,null,null,[value]);
        var route=new ValueDictionaryModule(1,"dictionary/300/200",1,"active",20,300,null,null,[],"common.palette");
        Write("common/palette.json",terminal);Write("dictionary/300/200.json",route);
        Write("common/apparel/color.json",module with { Values=[],ValuePath="dictionary.300.200" });
        Write("categories/100/types/200.json",new TypeFieldPlan(2,100,200,[new(20,"common.apparel.color")]));
        FieldBindingRow Confirm() {var row=service.CreateRequired(source).Single();row.Reviewed=true;return row;}
        var row=Confirm();service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId==101&&row.ValueSourcePaths.SequenceEqual(["common.apparel.color","dictionary.300.200","common.palette"]),
            "Dotted references resolve a type/module/dictionary/common chain and export its full trace");
        Write("common/palette.json",terminal with {Revision=2,Values=[value with {Revision=2,ValueId=102,TargetValue="更新值"}]});
        service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId==102&&row.Reviewed,"Editing only a shared terminal dictionary refreshes valueId without changing bindings");
        Write("common/palette.json",terminal with {Values=[],ValuePath="common.apparel.color"});
        service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null&&row.InputText=="酒红色"&&row.ValueStatus.Contains("循环"),
            "Reference cycles leave valueId blank while retaining confirmed source content");
        Write("common/palette.json",terminal with {Values=[],ValuePath="common.missing"});
        service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null&&row.ValueStatus.Contains("断链"),"Broken dictionary chains remain blank without searching other modules");
        Write("common/palette.json",terminal with {Status="disabled"});service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null&&row.ValueStatus.Contains("未启用"),"Disabled shared terminal dictionaries block their consumers");
        Write("common/palette.json",terminal with {DictionaryId=301});service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null&&row.ValueStatus.Contains("不一致"),"Every reference hop must preserve the target dictionary ID");
        Write("common/palette.json",terminal with {AttributeId=21});service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null&&row.ValueStatus.Contains("不一致"),"Every reference hop must preserve the target attribute ID");
        Write("common/palette.json",terminal with {DescriptionCategoryId=100,TypeId=201});service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null&&row.ValueStatus.Contains("不适用"),"Terminal dictionary applicability is checked as well as the root module");
        Write("common/palette.json",terminal with {ValuePath="common.other"});
        try { service.ResolveConfirmedFields(source,[row]);throw new Exception("Expected mixed source rejection"); }
        catch (InvalidDataException) {check(row.ValueId is null,"A value dictionary cannot contain both local values and a reference");}
        Write("common/palette.json",terminal with {Values=[],ValuePath="../outside"});
        try {store.Load(100,200);throw new Exception("Expected invalid nested path rejection");}
        catch (InvalidDataException) {check(true,"Nested dictionary paths are validated before following them");}
        Write("common/palette.json",terminal with {Values=[],ValuePath="dictionary.depth0"});
        for(var i=0;i<9;i++)Write("dictionary/depth"+i+".json",terminal with {
            ModuleId="dictionary/depth"+i,Values=[],ValuePath="dictionary.depth"+(i+1) });
        service.ResolveConfirmedFields(source,[row]);
        check(row.ValueId is null&&row.ValueStatus.Contains("8 层"),"Excessive reference depth is bounded and leaves the value unresolved");
        Write("common/palette.json",terminal);
        Write("categories/100/types/201.json",new TypeFieldPlan(2,100,201,[new(20,"common.apparel.color")]));
        var second=source with {Target=source.Target with {TypeId=201}};
        var other=service.CreateRequired(second).Single();other.Reviewed=true;service.ResolveConfirmedFields(second,[other]);
        check(other.ValueId==101,"Multiple explicit type plans can reuse the same terminal dictionary without copying values");
        Write("common/apparel/color.json",module with {TargetFingerprint="outdated"});
        var outdated=service.CreateRequired(source).Single();outdated.InputText="酒红色";outdated.Reviewed=true;
        service.ResolveConfirmedFields(source,[outdated]);
        check(outdated.ValueId is null,"Explicit field modules retain Schema fingerprint validation");
        foreach(var invalid in new[] {
            new FieldBinding(20,"颜色",["颜色"],Conditions:[]),
            new FieldBinding(20,"颜色",[],Conditions:[new("颜色","equals","红","红色")])
        }) {
            Write("common/apparel/color.json",module with {Binding=invalid});
            try {store.Load(100,200);throw new Exception("Expected conflicting binding rejection");}
            catch(InvalidDataException) {check(true,"Empty or contradictory field binding alternatives are rejected");}
        }
    }
}
