# 分类规则匹配与 Ozon 字段组合（第一版）

当前规则目录是一个可运行的空框架。它允许真实商品调试从零开始，并在每次验证后以代码变更逐条增加规则；当前版本没有预置任何服装、电子或其他品类的业务映射。

## 规则边界

规则分成三层：

1. `CommonSemanticModule` 只定义可复用语义及 1688 来源标签，例如 `color` 可识别“颜色”“颜色分类”。它不能包含 Ozon 品类、属性 ID、字典 ID或具体商品值。
2. `CategoryCommon` profile 绑定一个 `description_category_id`，把公共语义键连接到该品类实际的 Ozon 属性 ID。它不能限定 `type_id`。
3. `TypeOverride` profile 同时绑定 `description_category_id` 和明确的 `type_id` 集合，只声明与品类通用规则不同的项。同一属性和作用域发生冲突时，类型补丁覆盖品类绑定。

因此，某条规则是否属于 common 或特定补丁由声明位置和机器校验决定，不由运行时猜测。目录加载时会拒绝“带 type_id 的 CategoryCommon”和“没有 type_id 的 TypeOverride”。

```csharp
var catalog = new CategoryRuleCatalog("1.0.1",
    [new("common.color", "color", ["颜色", "颜色分类"])],
    [
        new("category.keyboard", categoryId, RuleProfileLayer.CategoryCommon, new HashSet<long>(),
            [new("keyboard.color", "color", colorAttributeId,
                RuleValueScope.Variant, RuleValueStrategy.DictionaryLookup)]),
        new("type.keyboard.special", categoryId, RuleProfileLayer.TypeOverride,
            new HashSet<long> { typeId },
            [new("keyboard.color.direct", "color", specialColorAttributeId,
                RuleValueScope.Variant, RuleValueStrategy.DirectText)]),
    ]);
```

新增规则的验收顺序是：真实源 JSON 建立失败样本；确认目标 Schema 属性和作用域；优先复用公共语义模块；只有同品类多数类型成立时才加入 `CategoryCommon`；只对少数类型成立时写 `TypeOverride`；通过合同、字典、必填覆盖和 Ozon 草稿组合测试后再启用。首个样本不会自动把规则提升为通用规则。

## 当前执行链路

`CategoryRuleMatchingEngine` 先产生确定性建议和缺口。AI 仍接收当前完整合同以补齐空规则覆盖不到的字段；最终合并时，直接文本规则覆盖 AI 的同属性建议，字典规则允许 AI 从程序查询得到的真实候选中完成选择。合并结果再次经过 `ProductMappingValidator`。

`OzonFieldCompositionEngine` 只接受合同校验结果。它为每个真实来源 SKU 生成一个独立的 Ozon `items[]` 元素，将商品公共属性复制到每个元素，再用当前 SKU 属性覆盖同键值。复杂属性按 `complexInstanceKey` 分组。多个 SKU 因此是多个 offer；后续如需在 Ozon 前台合并成一张商品卡，应由品类规则把该 Schema 允许的分组属性写成一致值，而不是把多个 SKU 塞进一个 API item。

第一阶段没有处理图片、价格、库存、尺寸、重量和税率。组合结果会明确返回 `composition.commercial_fields_deferred`，`ReadyForSubmit` 保持 false，不能直接提交 Ozon。

## 界面查看

选择商品与 Ozon 类型并运行 AI 映射后，在“字段匹配 → 引擎结果 JSON”查看：

- `mapping`：AI 与确定性规则合并后的校验结果；
- `categoryRules`：本次命中的规则、证据和缺口；
- `ozonImportDraft`：逐 SKU 组合的 Ozon 属性草稿及阻断项。
