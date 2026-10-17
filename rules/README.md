# 规则引擎结构

规则文件路径由环境变量 `AUTO_MAGIC_RULES_DIRECTORY` 指定，未配置时默认为：

- 开发环境：仓库根目录下的 `rules/`
- 发布版本：`%LOCALAPPDATA%\AutoMagic\rules\`

## 文件结构

```
rules/
├── attributes/          # 通用属性规则，按属性 ID 命名
│   ├── 31.json          # 品牌
│   ├── 4295.json        # 俄罗斯尺码
│   ├── 4389.json        # 原产国
│   ├── 8229.json        # 商品类型 ID (system: type-id)
│   ├── 8292.json        # 卡片组 (system: card-group)
│   ├── 9163.json        # 性别
│   ├── 9533.json        # 厂商尺码（自由文本）
│   ├── 10096.json       # 产品颜色（字典）
│   └── 10097.json       # 颜色名称（自由文本，AI 翻译）
├── overrides/
│   ├── categories/      # 品类覆盖，按品类 ID 命名
│   │   └── {categoryId}.json
│   └── types/           # 类型覆盖，按类型 ID 命名
│       └── 93182.json   # 女装连衣裙：性别默认"女性"，尺码使用女装换算
├── conversions/         # 换算表，按名称命名
│   ├── size-women-apparel.json
│   └── size-men-apparel.json
└── dictionaries/        # 字典同义词，按字典 ID 命名
    ├── 320.json         # 性别 9163 (待完善)
    ├── 835.json         # 俄罗斯尺码 4295 (待完善)
    ├── 1494.json        # 商品颜色 10096 (待完善)
    └── 1935.json        # 原产国 4389 (待完善)
```

## 规则优先级

属性规则按以下三层合并，高层覆盖低层（字段级别，未设置的字段保留低层的值）：

1. `attributes/{attributeId}.json` — 通用规则（最低层）
2. `overrides/categories/{categoryId}.json` — 品类覆盖
3. `overrides/types/{typeId}.json` — 类型覆盖（最高层）

## 属性规则 schema

```jsonc
{
  "attributeId": 31,
  "name": "品牌",               // 显示名称，可选
  "fields": ["品牌"],           // 源字段列表，按优先级排序，先查 SKU 级 spec 轴，再查商品级 facts
  "fallback": "无品牌",         // 当所有源字段都缺失时使用的默认值，可选
  "system": null,               // "card-group" 或 "type-id"，系统生成规则，设置后其他字段无效
  "conversion": null,           // 换算表名称（conversions/{name}.json），可选
  "when": [                     // 条件分支，按顺序匹配，首个命中的分支生效
    {
      "source": "性别",         // 源字段名称（与 attribute 二选一）
      "attribute": null,        // 依赖的其他属性 ID（与 source 二选一）
      "equals": "女",           // 匹配值（source 或 attribute 的清洗后值）
      "value": "女性",          // 命中时返回的固定值（与 conversion 二选一）
      "conversion": null        // 命中时应用的换算表（与 value 二选一）
    }
  ],
  "otherwise": null             // 没有命中任何 when 分支时的值，可选；未设置则为空
}
```

## 覆盖文件 schema

品类和类型覆盖文件结构相同，只设置需要修改的字段（字段级覆盖）：

```jsonc
{
  "categoryId": 200000933,   // 品类覆盖必须设置 categoryId
  "typeId": null,            // 品类覆盖不设置 typeId
  "attributes": {
    "9163": {                // 属性 ID 作为键
      "fallback": "女性"      // 只覆盖 fallback，其他字段保留通用规则的值
    },
    "4295": {
      "when": [              // 完整替换 when 分支列表
        {
          "attribute": 9163,
          "equals": "女性",
          "conversion": "size-women-apparel"
        }
      ]
    }
  }
}
```

类型覆盖文件将 `categoryId` 改为 `typeId`。

## 换算表 schema

```jsonc
{
  "name": "size-women-apparel",
  "mappings": {
    "XS": "40",
    "S": "42",
    "M": "44",
    "L": "46",
    "XL": "48",
    "XXL": "50",
    "XXXL": "52"
  }
}
```

换算表中未列出的值在清洗阶段留空，等待人工补充。

## 字典同义词 schema

```jsonc
{
  "dictionaryId": 320,
  "entries": [
    {
      "valueId": 971082156,    // Ozon 字典值 ID
      "label": "女性",          // 规则文件和 UI 中使用的规范形式
      "match": [               // 源数据中可能出现的同义词列表
        "女",
        "女性",
        "女款",
        "女装"
      ]
    }
  ]
}
```

匹配时 `label` 和 `match` 列表均参与查找；人工维护时只需填写 `match` 数组。

同一个文本不能同时指向多个 valueId（加载时报错）。

## 字典适用性检查

字典值匹配后，引擎会查询 `%LOCALAPPDATA%\AutoMagic\dictionary-cache\{categoryId}\{typeId}\{dictionaryId}.json` 核对 valueId 是否适用于当前类目和类型。

缓存文件格式与 Ozon `/v1/description-category/attribute/values` 响应一致：

```json
{ "result": [ { "id": 971082156, "value": "Женский" }, ... ] }
```

缓存缺失时 `MatchingEngine` 返回 `Unmatched`，并在 note 中说明原因。

## 规则引擎行为

### 清洗阶段 (CleaningEngine)

生成中文值，按以下顺序执行：

1. 加载当前类型的完整属性规则（通用 + 品类覆盖 + 类型覆盖）
2. 对每个 SKU：
   - 拓扑排序所有属性（`DependencyResolver`），确保 `when.attribute` 依赖的属性先执行
   - 按排序顺序清洗每个属性：
     - `system: "card-group"` → `1688-{offerId}`
     - `system: "type-id"` → 当前 typeId
     - `when` 分支：按顺序检查条件，命中首个分支后停止，返回 `value` 或应用 `conversion`
     - 否则读取 `fields` 列表（先查 SKU spec 轴，再查商品 facts）
     - 应用 `conversion`（未列出的值留空）
     - 源字段缺失且无 `conversion` 时使用 `fallback`
3. 输出 `CleanedProduct`，包含每个 SKU 的中文属性值及来源信息

### 匹配阶段 (MatchingEngine)

将中文值转为 Ozon valueId：

1. 对每个清洗值：
   - 自由文本属性（`dictionaryId == 0`）：直接通过，状态 `FreeText`
   - 预设 valueId（`type-id`）：直接通过，状态 `Matched`
   - 字典属性：
     - 加载 `dictionaries/{dictionaryId}.json`
     - 查找 `label` 或 `match` 中的匹配项
     - 检查 valueId 是否在 `dictionary-cache` 中（适用性）
     - 命中且适用：状态 `Matched`
     - 命中但不适用或字典中无此值：状态 `Unmatched`
2. 输出 `MatchedProduct`，包含每个值的 valueId、状态和 `IsBlocking`（必填但无值）

### 错误处理

- **RuleConfigurationException**：规则文件错误（JSON 格式、循环依赖、缺少必填规则、换算表不存在）
- **必填属性无规则**：清洗前检查，缺少规则时抛出异常（配置错误，不是商品问题）
- **必填属性无值**：匹配后标记为 `IsBlocking`，商品不能提交，需人工补充

## UI 展示

`CleaningRow` 按属性和去重后的值分组（共享的商品级属性只显示一次），显示：

- 属性名称和 ID
- 级别：`商品`（所有 SKU 共享）或 `SKU`（来自 spec 轴）
- 影响的 SKU 数量
- 源字段和源值
- 清洗后的中文值
- valueId（匹配成功时）
- 状态：`已匹配` / `自由文本` / `未匹配` / `缺失`
- 阻断标记：必填但无值
- 详情：来源、换算表、note

## 规则变更报告要求

每当修改规则文件（添加、修改、删除属性规则、覆盖、换算表、字典同义词）时，需在任务最终响应中报告：

- 受影响的规则文件和范围（通用/品类/类型）
- 变更前后的行为差异
- 执行的验证（编译、测试、数据检查）

详见 `AGENTS.md` 中的"Rule change reporting"。
