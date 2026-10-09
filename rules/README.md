# 规则文件设计约定

本目录按已确认的理论方案整理。应用解析器尚未适配本格式，本文不代表应用已支持以下行为。

## 目录职责

```text
rules/
  common/
    index.json
    rules/
      gender.json
      country.json
  categories/
    200000933/
      index.json
      types/
        93182.json
```

- type 文件声明该类型的属性及显式引用，不维护实际值映射。当前 93182 文件只包含性别和国家两个属性，其他属性待后续确认补充，不视为完整上架方案。
- category/index.json 定义当前类目内的属性语义、引用和默认值。只有 type 显式引用的属性才会使用这里的配置，不进行隐式继承。
- common/index.json 将公共模块名称映射到实际文件。
- common/rules/*.json 保存中文 match、目标 targetValue 和 valueId。

## 路径解析

path 是逻辑引用，不是直接替换成文件路径的字符串：

- category 表示按当前 type.categoryId 加载的 categories/{categoryId}/index.json。
- common 表示 common/index.json。
- 点号后面的各段按 JSON 对象键精确读取；9163 是字符串属性键。semantic 用于说明属性含义，不用于搜索或猜测目标节点。
- 如果目标是属性配置对象，继续沿其 path 解析；如果目标是 common.modules 下的文件路径，则相对于 common/index.json 所在目录加载字典。
- 最终字典的 attributeId 必须与 type 声明的属性 ID 一致。公共字典是否可用于某一类目、类型还需结合目标 Schema 的字典和适用性核验，不能仅凭属性 ID 相同认定通用。
- 只读取声明的引用，不扫描其他目录补找。断链、循环、文件缺失和 JSON 结构错误属于配置错误，应明确报告。

当前性别链路：

```text
types/93182.json 的 attributes["9163"]
→ category.attributes.9163
→ common.modules.gender
→ common/rules/gender.json
→ 用输入中文查询 match，取得 targetValue/valueId
```

当前国家链路直接从 type 引用 common.modules.country，不经过 category 的属性配置。

## 统一默认值规则

默认值是匹配引擎的统一约定，属性只保存 defaultValue，不重复配置 defaultWhen。

1. 源值有效且唯一匹配时，使用源值的映射结果。
2. 源值缺失、未通过明确的有效性校验，或没有匹配项时，若当前引用链的属性配置声明了 defaultValue，使用默认值重新匹配同一字典。
3. 未声明默认值，或默认值无法唯一匹配时，结果留空。源值存在多个候选属于歧义，留空供人工处理，不选择第一项，也不使用默认值掩盖冲突。
4. 路径或文件等配置错误不触发默认值；默认值不能直接充当 valueId。

当前 defaultValue="女性" 位于类目 200000933 的属性 9163，因此任何显式引用该属性配置的 type 都会使用这一默认策略；直接引用 common.modules.gender 的 type 不会自动获得该默认值。

当前国家字典的重复 match 由人工后续处理，本次保留原始映射，不自动消歧。

源字段绑定、转换、有效性校验以及多层默认值冲突处理的具体配置结构尚待后续确认，本次不新增对应字段或执行逻辑。
