# JSON 规则与人工确认

当前桌面入口使用此规则库。经多个品类和类型验证、Ozon 属性 ID 与字典值 ID 均一致的属性放在 common；品类差异和类型差异分别覆盖。当前已配置公共性别规则，其他字段不能凭名称相似直接提升为公共规则。

```text
rules/
  common/{module}.json
  common/catalogs/{module}.json # Ozon 官方目标字典快照；不直接参与源值匹配
  categories/{description_category_id}/rules.json
  categories/{description_category_id}/types/{type_id}.json
  reviews.db       # 本机草稿、确认结果、审核、历史、待写入日志（不提交 Git）
```

加载顺序固定为 common → category → type。后层按 ruleId 使用 add/override/disable；override 完整替换一条字段规则，disable 禁用继承规则。多个规则争用同属性同作用域时报错，不按文件顺序猜测。品类/类型 JSON 格式见 rule-file.schema.json，公共模块见 common-rule-file.schema.json。

开发构建向上找到 AutoMagic.slnx 后使用仓库 rules；独立安装默认 `%LOCALAPPDATA%/AutoMagic/rules`。可用 `AUTO_MAGIC_RULES_DIRECTORY` 指定独立规则根目录。界面显示实际路径。规则库是当前 Windows 用户的本地数据，不是云端多租户服务。

## 使用方法

1. 采集商品并读取正确品类、类型的 Ozon Schema，点击“运行规则匹配”。
2. 自动匹配只使用清洗后已确认的标量事实和独立 SKU 事实。选中表单行，在底部选择来源；来源为空则仅保存当前商品人工事实。
3. 填写中文值。字典字段点击“确认解析并保存规则”会尝试当前有效映射、官方字典查询和精确解析；不能确定 ID 时保留候选、目标结果留空。
4. 人工选择官方候选后再确认；也可用“查询选中行字典”。中文搜索无结果时，可以在备用搜索词中填写俄文，而保留中文输入。没有自动翻译能力，不保证中文能搜到俄文字典。
5. 只对实际检查过的行勾选“已核对”。“保存草稿”不改变规则、不增加计数。“保存规则”取消后，确认只保存当前商品结果及对原建议的审核。
6. 确认会校验字典 ID、目标类型、作用域、复杂组和必填覆盖。未解决项可以保存进度，但正式目标值留空；仍不能直接上架。
7. 来源明确的确认写入当前 type 文件。改值只更新该源值的映射 revision；明确换来源才改变字段绑定。改成空白会暂停对应旧映射，不生成默认空值。来源缺失不产生通用默认规则。

## JSON 字段

品类/类型文件必须有 schemaVersion=1、categoryId、typeId（品类文件为 null）、rules。公共文件使用 schemaVersion=1、moduleId、rules。

字段规则：ruleId、revision、action、status、scope（product/sku）、sourceLabels、attributeId、dictionaryId、targetFingerprint、strategy（direct/dictionary）、values。

值映射：mappingId、revision、status、sourceValue、displayZh、targetValue、valueId（非字典为 null）。targetValue 与 valueId 必须是当前属性字典中的同一记录。中文 displayZh 是显示/查询内容，不替代官方值。

品类和类型规则的 `targetFingerprint` 由程序根据目标属性 ID、复杂组、类型、字典 ID、基数和必填标记计算。公共规则使用 `targetFingerprint: "*"`，运行时仍要求当前 Schema 的 attributeId 和 dictionaryId 与公共规则一致，随后继续执行类型、基数和字典合法性校验。只有经过多个品类/类型官方 API 验证的数据才能人工写入 common。单纯改文件而保留旧 revision 会污染统计；人工改规则内容必须递增对应 revision；程序还将规则内容指纹纳入统计身份，避免遗漏递增时继承旧分数。

status 为 active/trusted 时才允许自动应用；candidate、disabled、revalidation_required 留空。type action=disable 隐藏继承规则。active 是人工启用；trusted 是统计标签，实际信任由数据库审核统计计算，不接受仅改 JSON 标签绕过低正确率。

## 统计和恢复

字段绑定、值映射分别统计，按 category/type/规则版本/Schema 指纹隔离。样本以商品、来源作用域和来源值去重；商品公共属性在多个 SKU 复用不重复计数。规则内容和版本应同时变化。

至少 100 次有效人工审核、正确率 >=98% 显示可信；样本足够但低于98%时自动值留空。无审核不算正确。新人工规则立即 active，独立积累统计；人工纠正旧映射记为旧版错误，新版不继承旧分数。这里只实现审核驱动的学习，没有自动训练、模糊匹配或自动跨类型晋升。

SQLite 保存审核、商品确认结果及完整旧/新规则快照。规则写入前先持久化 pending，再原子替换 JSON；中断后加载会重试。若文件与 pending 冲突会阻止覆盖并明确报错。应同时备份 JSON 与 reviews.db，不要只备份其中一种。暂未提供图形化历史回滚界面。

当前表单每个 SKU 每个属性编辑一个值；复杂属性可以填写实例键，但不提供多值列表和重复复杂实例编辑。复杂/集合属性不自动生成可复用规则。直接字段不自动翻译。上架 API、模型训练、跨类型晋升均不在本轮实现范围。

类型 93182 在品类 200000933 下配置了属性 23079（字典 124413020）的连衣裙款式规则。原始列表中的“T恤连衣裙”和“褶”分别对应两个 valueId，未写入自动值映射；遇到这两个中文值时保持空白并等待人工选择。

## 公共字典目录

`common/catalogs` 保存从 Ozon 属性值接口完整读取的官方 `displayZh/targetValue/valueId`，与可自动执行的 `{module}.json` 规则分开。中文与俄文按 Ozon 返回的同一 `valueId` 对齐，不使用机器翻译。目录本身不直接参与匹配；同一中文名称对应多个 ID 时必须视为歧义并留空，只有人工消除歧义后才可写入执行规则。原产国目录绑定 `attributeId=4389`，当前仍为 `reference`，待从 Schema 确认 `dictionaryId` 后再建立可执行公共规则。

## 验证

`dotnet build AutoMagic.slnx`

`dotnet run --project verification/AutoMagic.RuleReview.Checks`

验证使用独立临时规则库和模拟字典，不读取密钥、不请求真实上架；包含 SQLite/文件落盘与 WPF 表单绑定渲染。
