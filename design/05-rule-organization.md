# 规则组织结构

## 规则复用策略：类目级复用

### 层级结构

```
rules/
├── global/                          # 全局通用（跨所有类目）
│   └── common.json                  # 极少数真正通用的规则
│
├── categories/                      # 类目级规则
│   ├── women-apparel/              # 女装大类
│   │   ├── brand.json              # 品牌映射
│   │   ├── color.json              # 颜色映射
│   │   ├── size.json               # 女装尺码换算
│   │   └── gender.json             # 性别（默认"女性"）
│   │
│   ├── men-apparel/                # 男装大类
│   │   ├── brand.json
│   │   ├── color.json
│   │   ├── size.json               # 男装尺码换算
│   │   └── gender.json             # 性别（默认"男性"）
│   │
│   └── kids-apparel/               # 童装大类
│       ├── brand.json
│       ├── color.json
│       ├── size.json               # 童装尺码换算
│       └── gender.json             # 性别（"男孩"/"女孩"）
│
└── types/                          # 类型级特殊处理（覆盖类目规则）
    ├── 93182-dress.json            # 连衣裙特殊规则（如果需要）
    └── 12345-tshirt.json           # T恤特殊规则（如果需要）
```

---

## 规则加载顺序（从宽到窄）

处理某个商品时，按以下顺序加载和合并规则：

```
1. 全局规则（global/common.json）
         ↓
2. 类目规则（categories/{category}/）
         ↓  后加载的规则覆盖前面的
3. 类型规则（types/{typeId}.json）
```

**例子**：处理"连衣裙93182"
1. 加载 `global/common.json`
2. 加载 `categories/women-apparel/*.json`（品牌、颜色、尺码、性别）
3. 加载 `types/93182-dress.json`（如果有特殊处理）

---

## 类目规则的内容

### 示例：categories/women-apparel/color.json

```json
{
  "categoryScope": ["women-apparel"],
  "attributeId": 10096,
  "attributeName": "商品颜色",
  "dictionaryId": 835,
  "sourceFields": ["颜色", "主要颜色"],
  "mappings": [
    { "source": "黑色", "valueId": 1111, "label": "Черный" },
    { "source": "白色", "valueId": 2222, "label": "Белый" },
    { "source": "红色", "valueId": 3333, "label": "Красный" },
    { "source": "本白", "valueId": 2222, "label": "Белый", "note": "合并到白色" },
    { "source": "米白", "valueId": 2222, "label": "Белый", "note": "合并到白色" }
  ]
}
```

### 示例：categories/women-apparel/size.json

```json
{
  "categoryScope": ["women-apparel"],
  "attributeId": 4295,
  "attributeName": "俄罗斯尺码",
  "dictionaryId": 1494,
  "sourceFields": ["尺码", "尺寸"],
  "conversionTable": [
    { "source": "S", "valueId": 5555, "label": "42" },
    { "source": "M", "valueId": 6666, "label": "44" },
    { "source": "L", "valueId": 7777, "label": "46" },
    { "source": "XL", "valueId": 8888, "label": "48" },
    { "source": "XXL", "valueId": 9999, "label": "50" }
  ],
  "note": "女装国际码到俄罗斯码的标准换算"
}
```

---

## 类型规则的使用场景

**何时需要类型级规则**：

1. **覆盖类目默认值**
   - 女装大类默认性别"女性"
   - 但"女童连衣裙"需要覆盖为"女孩"

2. **特殊属性处理**
   - 某个类型有独特的必填属性
   - 需要特殊的值映射逻辑

3. **例外情况**
   - 某个类型的尺码换算不同于大类标准

### 示例：types/93182-dress.json

```json
{
  "typeId": 93182,
  "typeName": "连衣裙",
  "inheritsFrom": "women-apparel",
  "overrides": {
    "4389": {
      "attributeName": "款式",
      "note": "连衣裙特有属性，AI处理"
    }
  }
}
```

---

## 类目的定义

### 如何划分类目？

不按Ozon的官方类目树划分（太细），而是按**商品属性的相似性**划分：

**服装类**：
- `women-apparel`：女装（连衣裙、T恤、裤子、外套...）
- `men-apparel`：男装
- `kids-apparel`：童装
- `underwear`：内衣（尺码体系不同）
- `shoes`：鞋类（尺码体系完全不同）

**其他类**：
- `electronics`：电子产品
- `home-decor`：家居用品
- ...

**判断标准**：
- 共享相同的颜色、尺码、品牌映射规则
- 必填属性结构相似

---

## 类目到Ozon类型的映射

需要维护一个配置文件，将Ozon的类型ID映射到规则类目：

### category-mapping.json

```json
{
  "93182": {
    "ozonTypeName": "连衣裙",
    "ruleCategory": "women-apparel"
  },
  "12345": {
    "ozonTypeName": "女士T恤",
    "ruleCategory": "women-apparel"
  },
  "67890": {
    "ozonTypeName": "男士T恤",
    "ruleCategory": "men-apparel"
  }
}
```

**使用方式**：
1. 用户选择Ozon类型93182
2. 系统查询映射表，找到 `ruleCategory: "women-apparel"`
3. 加载 `categories/women-apparel/` 下的所有规则

---

## 优势

1. **规则复用率高**
   - 一套女装规则，覆盖连衣裙、T恤、裤子等几十个Ozon类型
   - 新增一个女装类型，只需在映射表添加一行

2. **维护成本低**
   - 修改颜色映射，一次修改，所有女装类型生效
   - 不需要为每个Ozon类型单独维护规则

3. **扩展性强**
   - 新增类目：创建新的 `categories/{category}/` 目录
   - 特殊处理：添加 `types/{typeId}.json` 覆盖

4. **层次清晰**
   - 通用的放类目级
   - 特殊的放类型级
   - 规则文件职责明确

---

## 规则编写工作量（修正后的估算）

### 初期投入

**服装类（3个大类目）**：
- 女装：品牌(50) + 颜色(30) + 尺码(10) + 性别(2) = 92条规则
- 男装：品牌(50) + 颜色(30) + 尺码(10) + 性别(2) = 92条规则
- 童装：品牌(30) + 颜色(30) + 尺码(15) + 性别(2) = 77条规则

**总计**：约260条规则，覆盖服装类的大部分场景

**时间**：1-2周编写和测试

### 新增类目成本

- 新增一个Ozon类型（如"女士外套"）：只需在 `category-mapping.json` 添加一行
- 新增一个规则类目（如"鞋类"）：需要编写新的规则集，约80-100条规则

---

## 下一步

这个规则组织结构是否符合你的预期？或者需要调整哪些部分？
