# 数据库 Schema 设计（初稿，待修正后定稿）

状态：用户认可整体结构。下方"已知问题"是设计者自查出的缺陷，定稿前需逐条处理。

## 表清单

| 表 | 作用 |
|---|---|
| products | 1688 商品主记录，含状态、原始数据、规则结果、AI 结果 |
| product_skus | 每个规格组合一行，含 SKU 级属性、价格、库存、图片 |
| manual_edits | 人工修订记录，重新处理时重放 |
| ozon_mapping | 1688 商品/SKU 与 Ozon offer_id 的对照，及提交、审核状态 |
| category_type_mapping | Ozon 类型与规则类目的映射，缓存 Schema |
| dictionary_cache | Ozon 字典值缓存 |
| settings | 用户配置（人工审核开关、抓取数量、汇率、密钥） |
| tasks / task_products | 抓取批次及其商品 |

## 设计取舍

- 商品与 SKU 分表：商品级属性存一次，SKU 级属性逐行存。
- 复杂结构（source_data、processed_data、ai_data）用 JSON 列，只对状态、offer_id、类型建索引。
- 人工修订单独成表，不混进 processed_data，重新处理时由规则引擎优先读取。

## 已知问题（待处理）

1. 重复抓取时，"待提交"商品更新了 source_data，但已生成的 ai_data（俄文标题、描述、图片）来自旧数据，会过期。需要规定更新后清空或标记 ai_data。
2. 07 文档写"已放弃重新激活时清空 manual_edits"，与"保留人工修改"的原则不一致，需二选一。
3. manual_edits 按 (product, sku_key, attribute) 记录。规格名称或顺序变化后 sku_key 会失效。docs/pipeline-design.md 原方案是按（商品、属性、源值）保存，更稳。
4. processed_data（商品级）与 product_skus.attributes（SKU 级）重复存储同一类信息，需确定唯一来源。
5. ozon_offer_id 同时出现在 products、product_skus、ozon_mapping 三处，需确定唯一来源。
6. dictionary_cache 逐值存储。颜色等大字典按类型存储会很大，且既有实现对大字典只缓存搜索结果，需确认策略。
7. category_type_mapping 同时承担"规则类目映射"和"Schema 缓存"两件事，职责混杂，建议拆开。
8. 状态机缺少"被拒绝"的重抓取处理，以及 offer_id 在不同 Ozon 类型下重复使用的情况（products.offer_id 全局 UNIQUE）。

## 已确认的修正

### 问题 1：重复抓取后 AI 内容过期（已确认）

商品处于"待提交"等本地状态时，重复抓取到变化的 1688 数据，按变化类型分级处理：

| 变化内容 | 处理 |
|---|---|
| 标题、描述、图片 | 清空对应的 ai_data，状态退回"待AI处理"，重新生成 |
| 规格（颜色、尺码） | 重新跑规则引擎，ai_data 全部作废，状态退回"待AI处理" |
| 只有价格 | 只重算价格，ai_data 保留 |
| manual_edits | 一律保留，由规则引擎重新决定哪些仍适用 |

效果：AI 成本只花在内容真正变化的商品上，不会提交过期内容。

需同步修改 07-duplicate-handling.md 中"更新原始数据 + 保留人工修改"一节，补充 ai_data 的失效规则。

### 问题 2：已放弃商品重新激活时保留人工修改（已确认）

重新激活时不清空 manual_edits。新数据里不存在的源值，对应的修改自然不生效，不需要主动清除。

需同步修改 07-duplicate-handling.md：删除"已放弃"分支中的 `manual_edits: None`，以及"清理过期手动修改"的主动清除逻辑，改为由规则引擎按源值匹配时自然忽略。

### 问题 3：manual_edits 按源值记录（已确认）

主键由（商品、sku_key、属性）改为（商品、属性、源值）：

- 来自 1688 规格轴的值（颜色"香芋紫"、尺码"均码"），一条记录覆盖所有包含该值的 SKU。
- 商品级属性没有源值，源值留空，一条记录对应整个商品。
- 规格改名后，旧源值在新数据里不存在，该修改自然不生效。
- 个别 SKU 需要例外时，加可选的 sku_key 字段做覆盖，默认不用。

### 问题 4：商品级与 SKU 级属性各存一处（已确认）

属性归属看值的来源，不在规则里配置：

- products.processed_data 只存商品级属性（品牌、性别、材质等）。
- product_skus.attributes 只存来自 1688 规格轴的属性（颜色、尺码等）。
- 组装提交 JSON 时由代码把商品级属性合并进每个 SKU，数据库不存合并结果。

效果：每个属性只有一个存放位置，人工修改只需改一处。

### 问题 5：ozon_offer_id 只存一份（已确认）

- ozon_offer_id、ozon_product_id、ozon_task_id 只放在 ozon_mapping，products 和 product_skus 中的同名字段全部删除。
- ozon_mapping 用 product_skus.id 关联 SKU，不再用 sku_key 文本，供应商改规格名称后 offer_id 仍然对得上。
- offer_id 首次生成后写入 ozon_mapping 并设为只读，重复抓取、重新处理都不能修改。

### 问题 6：字典缓存分两档，匹配阶段不联网（已确认）

- 小字典（约 2000 条以内）：整本拉取，按（类目、类型、属性）存一份，记录拉取时间。
- 大字典（品牌等）：不整本镜像。只缓存规则引用过的 valueId 和人工选过的值，按需用 Ozon 搜索接口核验，通过后写入缓存。
- 颜色归入小字典档。2026-10-23 实测类型 93211 颜色字典 651 条，确认整本拉取；品牌（31）超过 5000 条，按需搜索，无品牌用"Нет бренда"（126745801）。AM 实现里缓存是 %LocalAppData%\AM\schemas\ 下的 JSON 文件（用户选定），不是 dictionary_fetch/dictionary_cache 表。
- 匹配阶段只读规则文件和本地缓存，不调 Ozon API。缓存缺失按"未匹配"阻断，不在匹配时偷偷联网。
- API 只在两个时点使用：选定类型后、抓取前拉取字典；人工处理阻断项时搜索字典。人工选定的值写入缓存和 manual_edits，用户点"加入规则"后才写入规则文件。

### 问题 7：拆分 category_type_mapping（已确认）

- 规则类目映射移出数据库，放入规则文件（如 rules/category-mapping.json），随 git 审计，符合 AGENTS.md 的规则变更报告要求。
- Schema 缓存单独建表 type_schema_cache，只存类目、类型、Schema 内容和拉取时间。
- 缓存的 Schema 必须含 is_required 和 is_aspect。缺这两个标记的旧缓存直接拒绝，要求刷新。
- tasks 引用类型 ID 时不再外键指向映射表，products 对 category_type_mapping 的外键同步删除。

### 问题 8：保持全局唯一，补"被拒绝"状态（已确认）

- products.offer_id 保持全局 UNIQUE：一个 1688 商品只对应一个 Ozon 类型，不支持同一源商品上到多个类型。8292 合并值为源商品 ID 的 36 进制编码（与 offer_id 前缀相同，见 10 号文档）。
- 状态机增加"被拒绝"（Ozon 审核拒绝）。重复抓取遇到"被拒绝"商品时按本地处理中处理：更新原始数据，保留人工修改，状态退回"待人工处理"，由人工根据 Ozon 返回的错误修正。
- Ozon 返回的属性错误按 offer_id 和属性 ID 回挂到人工补全（沿用 docs/pipeline-design.md 的做法）。
- 已知限制：想把同一款商品上到另一个类型，当前设计不支持。需要时再扩展唯一键，现在不做。

## 定稿后需同步修改的文档

- 07-duplicate-handling.md：补 ai_data 失效规则；删除已放弃分支清空 manual_edits 的逻辑；增加"被拒绝"行；manual_edits 改为按源值记录。
- 定稿 DDL 见 09-final-ddl.sql，已在 SQLite 中验证可建出 11 张表。本文上方初稿的 DDL 已作废，以 09 为准。
- 09 相对讨论稿新增了 dictionary_fetch 表，记录每个字典是整本拉取还是按需核验，用来区分"不适用"和"未知"。

<!-- APPEND-HERE -->
