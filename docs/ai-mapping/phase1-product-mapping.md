# 第一阶段：AI 商品属性映射预览

更新日期：2026-09-21。当前桌面入口使用 ProductMapping V2.1；历史 SemanticMapping V1 与第一代规则映射类已移除。

## 已确认边界

- 每次由用户选择一个已采集的 1688 商品。
- 对全部已确认真实存在的 SKU 生成建议，包括已确认但库存为零的 SKU；不在本阶段决定销售范围。未确认、身份重复或规格不完整的组合只展示问题，不构造笛卡尔积。
- Ozon 类目和类型由用户选择，读取对应 Schema 后固定用于本轮。
- AI 接收全部目标属性定义。必填属性必须给出建议或明确的未解决状态；可选属性只在源事实支持时填写。
- 只发送原始文本事实及已确认 SKU 规格；不发送图片、价格和库存草稿、旧默认原产国或旧映射规则。
- 只生成建议、SKU 身份计划、程序校验并展示。没有人工编辑保存、文案生成、Ozon 商品卡分组、规则学习、最终价格库存计算和上架提交。ReadyForListing 始终为 false。内部 productGroupKey 只关联同一个 1688 商品的变体，不会写入 Ozon 分组属性。

## 使用方法

1. 在“ Ozon Schema ”页配置 Ozon Client-Id、Api-Key 和 Qwen API Key，选择品类及商品类型，点击“验证并读取”。凭证沿用 Windows Credential Manager。
2. 完成一次包含详情的采集，或使用应用启动时恢复的最近采集批次。在“字段匹配”选择一个详情商品。
3. 点击“运行 AI 映射”。首轮提出映射；只有存在 dictionary_pending 且查询取得候选时才进行第二轮，最多两轮模型请求。
4. 查看“AI 映射建议”“校验与未解决问题”“源商品事实”。AI 输入、原始响应、完整校验结果可在相邻页签查看。
5. 切换商品、类目、类型、凭证或点击“取消”会废弃本轮结果；晚到的旧响应不会覆盖当前预览。

映射建议只保留在本次会话，不保存为规则或发布数据。原有采集快照的保存和恢复功能仍可使用。

## 执行链路

FieldMatchingInput → ProductMappingInputBuilder → CategoryRuleMatchingEngine → ProductMappingRunner / QwenProductSemanticMapper → ProductMappingValidator → OzonFieldCompositionEngine → 只读结果表与 JSON 草稿。

- 输入构造器过滤旧策略和推断事实，保留原始字段值与来源路径。新采集只接收页面结构化数据中的真实组合；历史快照里已由 DOM 交互确认的 SKU 仍可读取，旧字段名会适配为规格名称并保留精确证据路径。
- 详情页先直接读取当前 DOM；只有属性缺失，或已发现 SKU 证据但没有确认组合时，才定向滚动到属性/SKU 区域重试。第一阶段不滚动到页面底部，也不以全页懒加载完成作为成功条件。
- 新采集结果以 facts 作为唯一规范属性集合，raw 不再重复保存 title、attributes 和 colorOptions；旧快照仍可按原合同读取。
- AM 内部请求保留采集批次、商品身份、快照指纹、证据路径、真实 SKU 和完整 Schema，用于本地校验与审计；这些字段不直接发送给模型。Qwen 只接收品类文本、`字段：值`形式的商品公共事实和逐变体事实、目标属性定义、字典候选，以及本次请求内用于机械回填的短关联键 `eN/vN`。
- 模型返回的短关联键只发生大小写变化且仍能唯一对应时，程序执行机械恢复；未知或不唯一的别名继续按伪造证据拒绝。
- 所选类型与明确源事实存在高置信度冲突时，在调用 AI 前阻断。例如“无袖连衣裙”与“袖长：长袖”不能进入同一轮映射。
- 每个真实 SKU 都获得确定性的 variantKey 和不超过 50 字符的 merchantSku；身份依据依次为 1688 sourceSkuId、源规格 ID、排序后的原始规格名和值。原始 sourceCombinationKey 单独保留供审计，规格顺序变化不会改变身份。身份冲突的组合全部保留为问题，不会任选一个继续映射。
- 采集来源可追溯、规格结构完整且身份不冲突的组合属于有效 SKU。采集层保留“9号”“均码”“36键”等原始规格值，不在脱离品类规则时猜测或丢弃；规格值是否能映射到 Ozon 由后续品类规则判断。库存为 0 的真实组合仍是有效 SKU，并保留 unavailable/stock=0 状态。
- 首轮不预填任何旧语义规则。字典属性先给出带证据的查询文本，程序按类别、类型和属性向 Ozon 查询真实候选，按属性及查询文本去重。
- 分类规则目录当前为空，可在真实样本调试中逐条增加。公共语义模块不含 Ozon ID；品类通用 profile 绑定 `description_category_id`，type 补丁只声明例外并覆盖同一目标属性。规则命中优先于 AI 建议，所有合并结果仍执行同一合同校验。
- 模型首轮输出先经过机械归一化：可唯一确定的短事实编号补全为合同编号；没有候选时生成的字典 ID 被移除并退回 dictionary_pending；商品卡分组字段固定为 policy_required。这些修复不判断商品语义。
- 单个错误属性不会再阻断其他合法 dictionary_pending 的字典查询。只要请求身份正确，带有效目标属性及源证据的查询会继续执行；最终仍由完整校验器报告所有错误。
- 查不到候选与接口失败分别展示，不把它们解释为源商品缺少事实。
- 第二轮不是另一个 AI 的交叉验证，只是在取得真实 Ozon 字典 ID / 文本后让同一模型完成候选选择。若第二轮请求失败，保留已通过合同校验的首轮建议，字典属性仍保持待解析，不自动选择候选。
- 取消始终传播；模型返回不完整或结构无效的响应，不会被认作通过。

## 验证与状态

suggested 是建议状态，不是人工确认。其他状态为 dictionary_pending、missing_evidence、ambiguous、conversion_required、policy_required。

程序检查：

- 当前请求身份、Schema 属性 ID、完整且无重复的真实 SKU 集合。
- 引用事实存在，商品公共属性不能使用某个 SKU 的事实，SKU 属性不能使用其他 SKU 的事实。
- 有值的建议必须引用证据；缺失、歧义、待转换及待策略状态不能携带已选值。
- 字典 ID 和文本必须来自同一条当前候选；单值、多值数量及自由数值类型满足 Schema。
- 商品公共属性与逐 SKU 属性合并后，逐 SKU 检查必填覆盖及未解决项。
- 复杂属性按属性 ID 与实例键覆盖，保留其他实例，并检查各组必填成员。
- 不同源 SKU 的最终建议完全相同时提示检查，绝不自动合并或删除 SKU。

界面的“SKU 身份计划”页可在调用 AI 前查看商品组、商家 SKU、源 SKU、身份依据、规格和内部变体键。“引擎结果 JSON”显示规则命中、最终校验和逐 SKU 的 Ozon 属性组合草稿。“约束通过·待语义复核”只表示程序能检查的约束通过。即使 ID、引用和类型都合法，模型仍可能错误理解材质、颜色或其他含义；本阶段没有独立语义审核模型，也没有将 API 接受视为事实验证。

## 验证记录与限制

自动化验证命令：`dotnet test AutoMagic.slnx --no-restore --verbosity minimal`。

已覆盖真实 SKU 保留与未确认组合排除、DOM 采集兼容、原始证据隔离、可选属性、虚构字段/字典/证据、首轮响应机械归一化、局部错误不阻断合法字典查询、串 SKU、复杂属性继承、未解决必填统计、查询无结果/失败、第二轮失败保留首轮、取消与晚到响应、严格响应合同及 WPF 只读表格绑定。

测试使用模拟模型和字典响应，未调用真实百炼或 Ozon。实际模型准确率、真实账户接口可用性和首次真实商品体验仍需在桌面中验证，不能从自动化通过率推导。

构建预览版：

```powershell
dotnet publish src/AutoMagic.Desktop/AutoMagic.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/desktop-phase1 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

输出包含 AutoMagic.Desktop.exe 及 Data/ozon-category-tree.test.json。Chrome 插件 v0.1.19 使用直接读取与定向重试策略，并将详情缓存版本提升到 v8；复测前需在 chrome://extensions 重新加载已解压的插件。原有 Native Host 注册和桥接保持不变。
