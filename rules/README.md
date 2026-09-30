# JSON 规则与人工确认

## 第一阶段试用（2026-09-30）

当前版本仅执行当前 Ozon Schema 中 `isRequired=true` 的属性。规则文件可继续保存非必填映射，但生成方案和导出流程不显示、不执行这些映射。必填属性没有配置映射时仍显示一行空值，状态为“必填项未配置映射，待填写”，避免因为缺少规则而从表单中消失。

200000933/93182 当前有 6 个必填属性：类型 8229、性别 9163、俄罗斯尺码 4295、商品颜色 10096、合并至一张卡片 8292、服装和鞋类品牌 31。“合并至一张卡片”是商品变体的共享分组键，不是布尔值：同一款商品的不同颜色和尺码使用完全相同的字符串，每个变体仍保留不同的 `offer_id`，并分别填写颜色和尺码；不需要合并时也填写只属于该商品的唯一字符串。分组键不应包含品牌、类型、颜色或尺码。当前没有足够的人工命名规则，因此属性 8292 保持为空等待填写，不从标题或内部 SKU 自动猜测。

条件字段使用 conditions 代替 match，按顺序检查 field/operator/value/result。支持 equals、notEquals 和 contains，均要求该字段存在唯一非空的清洗后值。contains 是区分大小写的文本包含匹配，不使用 AI 或近似拼写；所有关联来源必须只有一个不同值且无同名不确定事实，才允许包含转换。没有命中时使用显式 fallbackValue（包括来源缺失或冲突），没有配置则留空；Schema 无效时即使有兜底也留空。条件也可使用 defaultValue，但仅在来源缺失且无同名不确定事实时填充。条件、默认和兜底均需人工确认，导出 resolution=condition/default/fallback。

200000933/93182 的原产国属性 4389：仅检查“原产国”“产地”，包含广州、深圳、东莞、汕头、杭州任一名称时转换为“中国”，因此“广州广东”也会命中，且保留原始来源与事实 ID。来源缺失、非空值未命中（例如“浙江”）或存在冲突时，均使用 fallbackValue“中国”。这是当前业务明确指定的全量兜底；它也会把“日本”等未命中值预填为“中国”，因此结果仍需人工确认。本阶段不解析 valueId。

200000933/93182 的类型属性 8229 已由“裙型”直接取值改为：袖型等于“无袖”时输出“无袖连衣裙”，否则兜底“连衣裙”。只读取“袖型”，不自动改用“袖长”；此规则不修改所选 Ozon typeId。

开发期间，成功请求的中文 Ozon Schema 缓存到 %LOCALAPPDATA%/AutoMagic/schema-cache/{categoryId}/{typeId}.zh-Hans.json。启动恢复类型或切换类型时自动读取；不自动过期、不自动联网刷新。“请求并刷新 Schema”始终请求官方接口并更新缓存。缓存损坏时显示错误并等待手动刷新；文件不包含凭证。此机制只用于测试/开发，正式发布前需重新评估时效策略。

字段清单可声明 defaultValue：仅在没有匹配来源、没有同名不确定事实且 Schema 有效时预填，并显示“规则默认值，待确认”；不覆盖现有值或冲突。200000933/93182 的性别（9163）默认值为女性。导出 usedDefault 标记区分默认值与采集事实；仍需人工确认。

桌面主按钮为“生成初步方案”：读取当前 Schema 后，按清洗后的事实进行字段关联，不查询 valueId。公共字段显示一次，SKU 字段按相同源值分组；多个不同来源值留空并展示候选。修改中文内容、勾选已确认后，可导出确认 JSON。未确认项的 confirmedValue 为 null；导出不代表已通过字典校验或能够上架。

字段清单保存在 categories/{categoryId}/bindings.json（typeId 为 null）和 categories/{categoryId}/types/{typeId}.bindings.json。采用 descriptionCategoryId、typeId、attributes（id、name、match）格式；类型按属性 ID 覆盖品类，单文件重复 ID 拒绝加载。目标 ID 必须存在于当前 Schema，复杂属性暂不支持确认。当前录入 200000933/93182 的 11 项映射，长度只匹配裙长。

字段清单与现有值规则共用 rules 根目录。第一阶段只生成中文方案，尚不自动回写字段或值规则、不解析字典；下文描述的字典确认服务保留用于后续阶段，旧表单已从主流程隐藏。

字段清单和值规则共用此目录，但第一阶段只读取 bindings.json / *.bindings.json。经多个品类和类型验证、Ozon 属性 ID 与字典值 ID 均一致的属性放在 common；品类差异和类型差异分别覆盖。当前已配置公共性别规则，其他字段不能凭名称相似直接提升为公共规则。

```text
rules/
  common/{module}.json
  common/catalogs/{module}.json # Ozon 官方目标字典快照；不直接参与源值匹配
  categories/{description_category_id}/bindings.json
  categories/{description_category_id}/types/{type_id}.bindings.json
  categories/{description_category_id}/rules.json
  categories/{description_category_id}/types/{type_id}.json
  reviews.db       # 本机草稿、确认结果、审核、历史、待写入日志（不提交 Git）
```

值规则服务的加载顺序为 common → category → type；第一阶段字段清单仅按品类 → 类型继承。后层按 ruleId 使用 add/override/disable；override 完整替换一条字段规则，disable 禁用继承规则。多个规则争用同属性同作用域时报错，不按文件顺序猜测。品类/类型 JSON 格式见 rule-file.schema.json，公共模块见 common-rule-file.schema.json。

开发构建向上找到 AutoMagic.slnx 后使用仓库 rules；独立安装默认 `%LOCALAPPDATA%/AutoMagic/rules`。可用 `AUTO_MAGIC_RULES_DIRECTORY` 指定独立规则根目录。界面显示实际路径。规则库是当前 Windows 用户的本地数据，不是云端多租户服务。

## 当前操作

1. 选择已采集商品及 Ozon 品类、类型，读取缓存或手动刷新 Schema。
2. 点击“生成初步方案”，检查字段关联、来源值和条件/默认值提示。
3. 修改中文内容，勾选实际确认的分组，点击“导出已确认 JSON”。
4. 未确认条目导出为 confirmedValue=null；导出保留来源事实 ID、SKU 范围和 resolution。当前不解析 valueId、不写入审核统计、不自动生成或修改规则。

## 后续阶段保留的值规则服务

以下格式和统计描述对应 RuleReviewService / JsonRuleReviewStore，服务有集成验证，但当前新界面尚未接通。不能把服务能力当作第一阶段可操作功能。

## 值规则 JSON 字段

品类/类型文件必须有 schemaVersion=1、categoryId、typeId（品类文件为 null）、rules。公共文件使用 schemaVersion=1、moduleId、rules。

字段规则：ruleId、revision、action、status、scope（product/sku）、sourceLabels、attributeId、dictionaryId、targetFingerprint、strategy（direct/dictionary）、values。

值映射：mappingId、revision、status、sourceValue、displayZh、targetValue、valueId（非字典为 null）。targetValue 与 valueId 必须是当前属性字典中的同一记录。中文 displayZh 是显示/查询内容，不替代官方值。

品类和类型规则的 `targetFingerprint` 由程序根据目标属性 ID、复杂组、类型、字典 ID、基数和必填标记计算。公共规则使用 `targetFingerprint: "*"`，运行时仍要求当前 Schema 的 attributeId 和 dictionaryId 与公共规则一致，随后继续执行类型、基数和字典合法性校验。只有经过多个品类/类型官方 API 验证的数据才能人工写入 common。单纯改文件而保留旧 revision 会污染统计；人工改规则内容必须递增对应 revision；程序还将规则内容指纹纳入统计身份，避免遗漏递增时继承旧分数。

status 为 active/trusted 时才允许自动应用；candidate、disabled、revalidation_required 留空。type action=disable 隐藏继承规则。active 是人工启用；trusted 是统计标签，实际信任由数据库审核统计计算，不接受仅改 JSON 标签绕过低正确率。

## 统计和恢复

字段绑定、值映射分别统计，按 category/type/规则版本/Schema 指纹隔离。样本以商品、来源作用域和来源值去重；商品公共属性在多个 SKU 复用不重复计数。规则内容和版本应同时变化。

至少 100 次有效人工审核、正确率 >=98% 显示可信；样本足够但低于98%时自动值留空。无审核不算正确。新人工规则立即 active，独立积累统计；人工纠正旧映射记为旧版错误，新版不继承旧分数。这里只实现审核驱动的学习，没有自动训练、模糊匹配或自动跨类型晋升。

SQLite 保存审核、商品确认结果及完整旧/新规则快照。规则写入前先持久化 pending，再原子替换 JSON；中断后加载会重试。若文件与 pending 冲突会阻止覆盖并明确报错。应同时备份 JSON 与 reviews.db，不要只备份其中一种。暂未提供图形化历史回滚界面。

后续字典服务暂不支持复杂/集合属性自动生成可复用规则，不自动翻译。正式上架、模型训练、跨类型晋升未实现。

类型 93182 在品类 200000933 下配置了属性 23079（字典 124413020）的连衣裙款式规则。原始列表中的“T恤连衣裙”和“褶”分别对应两个 valueId，未写入自动值映射；遇到这两个中文值时保持空白并等待人工选择。

## 公共字典目录

`common/catalogs` 保存从 Ozon 属性值接口完整读取的官方 `displayZh/targetValue/valueId`，与可自动执行的 `{module}.json` 规则分开。中文与俄文按 Ozon 返回的同一 `valueId` 对齐，不使用机器翻译。目录本身不直接参与匹配；同一中文名称对应多个 ID 时必须视为歧义并留空，只有人工消除歧义后才可写入执行规则。原产国目录绑定 `attributeId=4389`，当前仍为 `reference`，待从 Schema 确认 `dictionaryId` 后再建立可执行公共规则。

## 验证

`dotnet build AutoMagic.slnx`

`dotnet run --project verification/AutoMagic.RuleReview.Checks`

验证使用独立临时规则库和模拟字典，不读取密钥、不请求真实上架；包含 SQLite/文件落盘与 WPF 表单绑定渲染。
