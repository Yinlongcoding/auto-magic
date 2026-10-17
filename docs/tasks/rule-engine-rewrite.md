# 规则引擎重构任务

## 任务目标

将清洗和匹配规则从按类型组织改为按属性 ID 组织，实现条件推导和依赖排序，删除旧引擎，创建新引擎。

## 当前状态（2026-10-16）

### 已完成
- ✅ Schema 的 `is_aspect` 解析已存在且正常工作
- ✅ 删除了旧 Schema 缓存，用户已重新拉取（目前有 93211 的缓存）
- ✅ 删除了 `dress-type.json` 规则及其引用
- ✅ 确定了 8229 由系统生成，值为当前 type_id
- ✅ 确定了原产国（4389）是全局字典，`category_dependent: false`
- ✅ 用户确认类型按批次人工选择（A 方案），不自动分配

### 当前数据
- 有 10 个采集好的 1688 商品用于验证
- Schema 已刷新（有 93211，需要再请求 93182）
- 目标类目 200000933，类型 93182（连衣裙）

## 规则引擎设计（已确认）

### 文件组织

```text
rules/
  attributes/{attributeId}.json      通用属性规则，跨类目、类型复用
  overrides/categories/{categoryId}.json   类目例外
  overrides/types/{typeId}.json            类型例外（优先级最高）
  conversions/{name}.json                  换算表，只能被例外显式引用
  dictionaries/{dictionaryId}.json         字典同义词，人工只维护 match 数组

%LOCALAPPDATA%/AutoMagic/
  schema-cache/{categoryId}/{typeId}.json              属性清单从这里读取
  dictionary-cache/{categoryId}/{typeId}/{dictionaryId}.json   适用范围检查
  user-data.db                                        修订、offer_id 对照表
```

### 规则优先级

1. 类型例外（overrides/types/）
2. 类目例外（overrides/categories/）
3. 通用属性规则（attributes/）
4. 都没有：必填项留空阻断，非必填不提交

### 规则字段说明

**通用规则示例**：
```json
// attributes/4389.json
{ "attributeId": 4389, "name": "原产国", "fields": ["原产国", "产地"] }

// attributes/31.json
{ "attributeId": 31, "name": "服装和鞋类品牌", "fields": ["品牌"], "fallback": "无品牌" }

// attributes/8292.json
{ "attributeId": 8292, "name": "合并至一张卡片", "system": "card-group" }

// attributes/8229.json
{ "attributeId": 8229, "name": "类型", "system": "type-id" }
```

**例外规则示例**：
```json
// overrides/types/93182.json
{
  "typeId": 93182,
  "attributes": {
    "9163": { "fallback": "女性" },
    "4295": {
      "when": [
        { "attribute": 9163, "equals": "女孩", "conversion": "size-girls" },
        { "attribute": 9163, "equals": "女性", "conversion": "size-women-apparel" }
      ]
    }
  }
}
```

**字段含义**：
- `fields`: 去源数据的哪个字段取值，先查规格轴（命中就是 SKU 级），再查商品属性
- `fallback`: 源字段缺失时用什么值，只在缺失时使用
- `system`: 系统生成，`"card-group"` 生成 8292，`"type-id"` 生成 8229
- `conversion`: 换算表名称，取值后查表转换
- `when`: 条件推导，按顺序判断，命中第一条就停
- `otherwise`: 推导规则自己的默认值，不写就是留空

**条件推导**：
- `source`: 读源字段，`{ "source": "袖型", "equals": "无袖", "value": "..." }`
- `attribute`: 读另一个属性的结果，`{ "attribute": 9163, "equals": "女性", "conversion": "..." }`

### 清洗与匹配流程伪代码

```text
for 属性 in Schema.attributes:
    规则 = attributes/{id} + overrides/categories + overrides/types  # 按优先级合并

    # 清洗：得到中文值
    if 规则.system == "card-group": 值 = 1688-{源商品offerId}
    elif 规则.system == "type-id": 值 = 当前 type_id
    elif 规则.when: 值 = 按条件推导
    else:
        值 = 源数据.规格轴[规则.fields] 或 源数据.商品属性[规则.fields]
        if 值 缺失: 值 = 规则.fallback
    if 规则.conversion: 值 = 换算表[规则.conversion][值]

    # 匹配：得到 valueId
    if 属性.dictionaryId == 0: 结果 = 值  # 自由文本
    else:
        结果 = 字典[dictionaryId] 中 label==值 或 match 包含值 的 valueId
        if 结果不在 dictionary-cache(类目, 类型): 结果 = 空  # 适用范围检查

    # 判定
    if 结果 为空 and 属性.isRequired: 阻断
    if 结果 为空 and 非必填: 不提交
```

### 已明确的属性处理

| 属性 ID | 名称 | 通用规则 | 例外 |
| --- | --- | --- | --- |
| 8292 | 合并至一张卡片 | system: card-group | 无 |
| 8229 | 类型 | system: type-id | 无 |
| 31 | 品牌 | 取"品牌"，缺失填"无品牌" | 无 |
| 9163 | 性别 | 取"性别/适用性别" | 93182 缺失填"女性" |
| 4295 | 俄罗斯尺码 | 取"尺码/尺寸" | 93182 按性别换算 |
| 9533 | 制造商尺码 | 取规格轴尺码，原样写入 | 无 |
| 10096 | 商品颜色 | 取规格轴"颜色/颜色分类" | 无 |
| 10097 | 颜色名称 | 取规格轴颜色值，T 步 AI 翻译 | 无 |
| 4389 | 原产国 | 取"原产国/产地" | 无 |

## 实施步骤

### 1. 删除旧引擎和规则（清理阶段）

删除以下目录和文件：
```
rules/clean/
rules/match/
src/AutoMagic.Application/Ozon/Cleaning/
src/AutoMagic.Infrastructure/Ozon/Cleaning/
src/AutoMagic.Desktop/ViewModels/MainViewModel.Cleaning.cs
```

保留：
- `rules/README.md`（需要更新内容）
- `src/AutoMagic.Application/Ozon/OzonCategorySchema.cs` 和相关 Schema 代码
- 所有采集、Schema 缓存相关代码

### 2. 创建新引擎（核心实现）

新建以下文件：
- `src/AutoMagic.Application/Ozon/Rules/AttributeRule.cs` - 规则数据结构
- `src/AutoMagic.Application/Ozon/Rules/RuleLoader.cs` - 规则加载器（三层合并）
- `src/AutoMagic.Application/Ozon/Rules/DependencyResolver.cs` - 依赖排序和循环检测
- `src/AutoMagic.Application/Ozon/Rules/CleaningEngine.cs` - 清洗引擎
- `src/AutoMagic.Application/Ozon/Rules/MatchingEngine.cs` - 匹配引擎
- `src/AutoMagic.Infrastructure/Ozon/Rules/JsonRuleStore.cs` - 规则文件加载

### 3. 创建规则文件

按照上述 7 个已明确的属性，创建：
- `rules/attributes/4389.json` 到 `rules/attributes/10097.json`（8 个通用规则）
- `rules/overrides/types/93182.json`（类型例外）
- `rules/conversions/size-women-apparel.json`（女装尺码换算表，数值待定）
- `rules/dictionaries/1935.json`、`320.json`、`1494.json`、`835.json`（字典同义词骨架）

### 4. 更新桌面端

修改 `src/AutoMagic.Desktop/ViewModels/MainViewModel.cs`：
- 移除旧的 `ProductCleaningService` 调用
- 接入新的 `CleaningEngine` 和 `MatchingEngine`
- 输出结果保持兼容（或更新 UI 显示）

### 5. 验证

用 10 个采集好的商品跑一遍新引擎：
- 清洗输出中文值
- 匹配输出 valueId
- 检查必填属性是否都有值或正确阻断
- 检查 SKU 级（规格轴）和商品级（商品属性）是否正确分层

## 规则变更报告（待完成时报告）

需要在任务完成时报告：
1. 删除了整个旧规则结构（clean、match 三层索引）
2. 创建了按属性 ID 组织的新规则结构
3. 8229 改为系统生成
4. 品牌兜底改为通用规则
5. 性别兜底改为类型例外
6. 新增女装尺码换算表
7. 更新 AGENTS.md：规则优先级由"不做隐式继承"改为"固定三层：类型→类目→通用"

## 注意事项

- 条件推导的依赖排序必须在运行时计算，不能依赖规则文件顺序
- 循环依赖必须在加载时检测并报错
- 字典的 `category_dependent` 决定缓存策略：false 全局缓存，true 按类目+类型缓存
- 9533（制造商尺码）和 10097（颜色名称）是自由文本（dictionaryId = 0），不做匹配
- 8292 的值是 `1688-{offerId}`，不是 `base36(offerId)`（后者是 offer_id 的生成规则）

## 相关文档

- [docs/pipeline-design.md](../pipeline-design.md) - 完整流程设计
- [AGENTS.md](../../AGENTS.md) - 项目指导原则
- [rules/README.md](../../rules/README.md) - 规则文件组织（需要更新）
- [C:\Users\Administrator\.claude\projects\d--ys-project-auto-magic\memory\project-am-pipeline-decisions.md](C:\Users\Administrator\.claude\projects\d--ys-project-auto-magic\memory\project-am-pipeline-decisions.md) - 用户决策记录
