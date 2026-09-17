# AM 字段匹配输入/输出结构

## 1. 目标

字段匹配模块读取采集模块已经落盘的固定输入，不重新访问 1688，也不修改原始详情 JSON。每个商品独立生成一份映射结果，供人工审阅和自动上架模块消费。

核心原则：

1. 原始中文、原始字段名和原始字段值保持不变。
2. 同名或重复事实均保留，以稳定的 `factId` 引用，不在输入阶段去重。
3. 确定性匹配优先，语义模型只负责产生候选，不能伪造源事实或 Ozon 字典 ID。
4. 原始事实、候选值、最终值和人工确认状态分开保存。
5. 缺失、歧义、需要字典解析或外部换算的字段不得伪装成已完成。

## 2. 数据流

```text
details/NNN.json
  → 构建字段匹配输入
  → 确定性匹配
  → Ozon 字典合法候选解析
  → Qwen 受约束语义裁决
  → 版本化业务规则换算
  → 合并确定性、Qwen与转换结果
  → 最终合同与证据校验
  → 人工确认（必要时）
  → 字段匹配输出
  → 自动上架模块
```

## 3. 匹配输入

建议每个商品生成一个 `mapping-input.json`。顶层结构如下：

```json
{
  "contractVersion": "1.0",
  "mappingJobId": "MAP-20260915-000001",
  "collectionId": "COL-20260915065202970-4c0b8569264e476b94c023aef90b604c",
  "productRef": {
    "itemIndex": 1,
    "itemPosition": 2,
    "offerId": "1081315169880",
    "detailUrl": "https://detail.1688.com/offer/1081315169880.html",
    "capturedAt": "2026-09-15T06:51:24.374Z",
    "captureStatus": "success"
  },
  "source": {
    "platform": "1688",
    "language": "zh-CN",
    "title": "黑猫少女粉色键帽二次元动漫DIYPBT热升华个性四面透光机械键盘帽",
    "facts": [],
    "media": [],
    "priceEvidence": [],
    "skuEvidence": []
  },
  "target": {
    "platform": "Ozon",
    "descriptionCategoryId": 123,
    "typeId": 456,
    "categoryPath": "由用户确认的 Ozon 类目路径",
    "categoryAndTypeConfirmedByUser": true,
    "attributes": []
  },
  "rules": {
    "preserveSourceLanguage": true,
    "allowSyntheticFacts": false,
    "allowSyntheticSku": false,
    "requireEvidenceForMappedValue": true
  }
}
```

`descriptionCategoryId`、`typeId` 和属性 ID 必须来自实时 Ozon Schema；示例数字仅表示数据类型，不能直接用于上架。

### 3.1 源事实 `source.facts`

将详情 JSON 的 `facts` 按原顺序转换为带稳定编号的事实：

```json
[
  {
    "factId": "f001",
    "kind": "title",
    "label": "商品名称",
    "value": "黑猫少女粉色键帽二次元动漫DIYPBT热升华个性四面透光机械键盘帽",
    "source": "document.title",
    "sourcePath": "$.facts[0]"
  },
  {
    "factId": "f002",
    "kind": "attribute",
    "label": "材质",
    "value": "PC、PBT",
    "source": "decision-attributes",
    "sourcePath": "$.facts[1]"
  },
  {
    "factId": "f023",
    "kind": "attribute",
    "label": "材质",
    "value": "PC,PBT",
    "source": "cpv-attributes",
    "sourcePath": "$.facts[22]"
  }
]
```

即使 `f002` 和 `f023` 表达近似，也同时保留。匹配阶段可以引用两条证据，但不能改写它们。

### 3.2 图片证据 `source.media`

```json
[
  {
    "mediaId": "m001",
    "kind": "source_image",
    "url": "https://cbu01.alicdn.com/example.jpg",
    "position": 1,
    "sourcePath": "$.raw.imageUrls[0]"
  }
]
```

当前采集结果只能确认图片 URL 和顺序，不能无证据地标记为“主图”或“详情图”。

### 3.3 价格与 SKU 证据

当前采集器保存的是页面原始文本，因此先作为证据输入，不在输入阶段强行解析：

```json
{
  "priceEvidence": [
    {
      "evidenceId": "p001",
      "text": "新人价¥32.00首件预估到手价¥35.001套起批",
      "sourcePath": "$.raw.priceTexts[0]"
    }
  ],
  "skuEvidence": [
    {
      "evidenceId": "s001",
      "text": "暖黄黑猫少女四面透键帽¥35库存8888套",
      "sourcePath": "$.raw.skuTexts[2]"
    }
  ]
}
```

后续若采集器产出结构化阶梯价或真实 SKU，新增 `structuredPrices`、`structuredSkus`，原始文本仍保留用于审计。

### 3.4 目标属性 `target.attributes`

目标字段必须完整复制 Ozon 动态 Schema：

```json
[
  {
    "attributeId": 789,
    "attributeComplexId": 0,
    "name": "Материал",
    "description": "Ozon 返回的字段说明",
    "type": "String",
    "groupName": "Основные",
    "isCollection": true,
    "isRequired": true,
    "maxValueCount": 5,
    "dictionaryId": 0,
    "dictionaryCandidates": []
  }
]
```

有字典的属性只能从 `dictionaryCandidates` 中选择 `valueId`；没有提供候选时只能返回 `dictionary_pending`，不能猜测 ID。

## 4. 匹配输出

每个商品生成一个 `mapping-output.json`：

```json
{
  "contractVersion": "1.0",
  "mappingJobId": "MAP-20260915-000001",
  "collectionId": "COL-20260915065202970-4c0b8569264e476b94c023aef90b604c",
  "productRef": {
    "offerId": "1081315169880",
    "itemPosition": 2
  },
  "status": "review_required",
  "targetMappings": [
    {
      "attributeId": 789,
      "attributeName": "Материал",
      "status": "mapped",
      "evidence": [
        {
          "factId": "f002",
          "label": "材质",
          "value": "PC、PBT",
          "source": "decision-attributes"
        },
        {
          "factId": "f023",
          "label": "材质",
          "value": "PC,PBT",
          "source": "cpv-attributes"
        }
      ],
      "candidateValues": [
        {
          "text": "PC",
          "dictionaryValueId": null
        },
        {
          "text": "PBT",
          "dictionaryValueId": null
        }
      ],
      "finalValue": {
        "texts": ["PC", "PBT"],
        "dictionaryValueIds": []
      },
      "mappingMethod": "semantic_label",
      "confidence": 0.98,
      "decisionSource": "deterministic",
      "requiresHumanReview": false,
      "reason": "源字段“材质”与目标字段语义一致，两个页面区域提供一致证据。"
    }
  ],
  "unmappedEvidenceIds": ["f005", "p001", "s001"],
  "validation": {
    "schemaValid": true,
    "requiredAttributeCount": 1,
    "mappedRequiredCount": 1,
    "missingRequiredCount": 0,
    "reviewRequiredCount": 0,
    "readyForListing": false,
    "blockingReasons": ["尚未生成可上架的结构化 SKU 和售价"]
  },
  "warnings": [],
  "generatedAt": "2026-09-15T07:00:00Z"
}
```

## 5. 状态定义

单字段 `status`：

| 状态 | 含义 |
| --- | --- |
| `mapped` | 已有证据且值满足目标字段规则 |
| `dictionary_pending` | 已有文本候选，但尚未解析到 Ozon 字典值 |
| `missing_evidence` | 源数据没有支持该字段的事实 |
| `ambiguous` | 存在多个冲突候选，需要人工确认 |
| `conversion_required` | 已有证据，但必须通过明确规则进行单位、尺码等换算 |
| `policy_required` | 该值取决于店铺或上架策略，不能从商品事实推断 |
| `rejected` | 人工或验证器明确拒绝该候选 |

商品级 `status`：

| 状态 | 含义 |
| --- | --- |
| `mapped` | 所有必填字段均完成且不存在阻断项 |
| `review_required` | 至少一个必填字段需要人工、字典或规则处理 |
| `blocked` | 缺少必填事实、SKU、售价或其他上架必要数据 |
| `failed` | 输入损坏或映射流程执行失败 |

## 6. 匹配顺序

1. 将 `details/XXX.json` 转换为只读的标准化商品事实，并保留 `factId` 与 `sourcePath`。
2. 执行精确标签、已审核别名和安全包含关系等确定性匹配。
3. 根据已确认的 Ozon 类目、`typeId` 和属性 Schema，识别需要字典解析的字段。
4. 在调用 Qwen 前查询 Ozon 合法 `valueId` 候选；确定性精确命中的候选直接完成映射。
5. Qwen 只处理仍不明确的字段，并且只能从已提供的字典候选中选择 `valueId`。
6. 执行版本化业务换算、平台策略和最终合同校验。
7. 证据不足、候选冲突或外部规则缺失时进入人工确认。

高置信度不等于可以绕过目标字段约束。字典值、尺码换算、默认品牌和店铺策略必须分别经过字典、规则或人工确认。

## 7. 与当前代码的关系

当前 `FieldMatchingInput` 是原始详情到通用引擎的标准化输入，`FieldMatchingEnginePlan` 负责记录确定性匹配、字典查询、Qwen裁决和最终校验的阶段状态。`SemanticMappingRequest`、`SemanticMappingResponse` 和严格响应验证器只作为“Qwen 语义裁决”子步骤使用。正式字段匹配合同还需要在其外层持续补充：

- `collectionId`、`productRef` 和商品级状态；
- 图片、价格文本和 SKU 文本证据；
- `sourcePath` 审计定位；
- 确定性匹配与 Qwen 候选的决策来源；
- 最终值、人工确认状态和上架就绪验证。

Qwen或转换器的输出都不能直接作为自动上架输入。`FieldMatchingFinalOutputMerger` 按“确定性结果优先、转换结果只处理已授权转换字段、Qwen只处理AI待决字段”的顺序合并；任何阶段引用不存在的`factId`、覆盖已确定字段或选择候选范围之外的`valueId`都会拒绝合并。只有通过结构校验、证据校验、Ozon字典校验和必要人工确认后的`finalValue`才能进入下一阶段。

统一输出中的`readyForNextStage`只表示目标字段匹配已经完成；它不等于可以直接上架。结构化SKU、售价、图片、库存等条件需要由自动上架阶段独立校验，因此当前`readyForListing`保持为`false`。
