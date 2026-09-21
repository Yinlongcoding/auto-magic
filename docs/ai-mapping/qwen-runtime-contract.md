# Qwen 映射运行合同

## 已冻结配置

- 服务商：阿里云百炼（DashScope）
- 地域：华北2（北京），地域ID `cn-beijing`
- 调用方式：OpenAI 兼容 API
- 生产映射模型：`qwen3.7-plus-2026-05-26`
- 探索性测试模型：允许使用滚动别名 `qwen3.7-plus`
- 开发共享 Base URL：`https://dashscope.aliyuncs.com/compatible-mode/v1`

生产映射不得默认使用滚动别名。每次映射记录必须保存实际 `modelId`、Skill版本、输入合同版本和请求ID，以保证结果可复现、可审计。

## 模型调用边界

- 使用 `product-mapping-v2-prompt.txt` 作为系统约束，使用符合 ProductMapping V2.1 合同的详情事实JSON作为用户数据。
- 关闭联网、工具调用和外部知识检索，防止模型引入当前商品事实之外的信息。
- 使用模型支持的最低随机性；请求结构化JSON输出，并以 `product-mapping-response.v2.schema.json` 作为公开结构合同。
- 本地 `ProductMappingValidator` 在严格JSON反序列化之后继续校验JSON Schema无法表达的跨字段规则，包括SKU完整性、证据作用范围、状态依赖、属性白名单和字典ID白名单。
- 模型响应不是最终Ozon发布参数。字典解析、转换规则、业务策略和最终编译必须由本地确定性代码完成。
- 任何未通过本地Schema和证据一致性校验的响应都不得写入品类映射方案库。

## 凭证边界

- DashScope API Key不得写入代码仓库、普通JSON、日志、异常消息、映射方案或云端映射备份。
- 开发联调时由Auto Magic界面录入，并存入当前Windows用户的Windows Credential Manager。
- 普通本地配置只保存服务商、地域、Base URL、模型ID和Skill版本等非密钥数据。
- API Key必须与华北2（北京）的Base URL匹配；切换地域时必须显式更换对应凭证和Base URL。

## 版本策略

1. 新模型先以独立评测运行，不直接覆盖生产模型。
2. 使用已冻结样本集比较Schema合规率、字段正确率、不确定性识别率和调用成本。
3. 评测通过并经用户确认后，才更新生产模型ID和运行合同版本。
4. 已生成的映射方案保留其原始模型、Skill和输入合同版本，不因默认模型升级而改写历史记录。

## 当前桌面端调用流程

2026-09-20 起使用 ProductMapping V2，完整边界见 [第一阶段说明](phase1-product-mapping.md)。历史 SemanticMapping V1 合同和运行服务已移除。

1. 用户在“Ozon Schema”页录入 Ozon 和 Qwen 凭证，选定品类/类型并读取动态 Schema。
2. 在“字段匹配”选定已采集的一个商品；也可使用启动时恢复的最近采集批次。
3. 用户点击“运行 AI 映射”才调用百炼。首轮使用原始文本事实、全部已确认真实 SKU 和全部目标属性定义，不执行旧规则或默认值。
4. 使用 product-mapping-v2-prompt.txt、product-mapping-response.v2.schema.json 和 ProductMappingResponse 合同；保留固定模型、严格 JSON 输出及关闭联网/工具调用的设置。
5. 先校验首轮响应，再针对 dictionary_pending 的文本查询 Ozon；取得真实候选时才执行第二轮，最多两轮。第二轮失败保留首轮建议及未解决状态。
6. Qwen 使用独立的紧凑传输合同。内部 sourcePath、批次、商品标识、指纹、商家 SKU、源 SKU 和组合键不发送；`eN/vN` 只用于把响应机械还原到本地事实和变体，不作为语义上下文。两轮调用属于同一模型的建议与字典细化，不构成独立 AI 交叉验证。
6. ProductMappingValidator 检查证据作用范围、属性及字典、类型、多值、复杂组和逐 SKU 必填覆盖。合同合法不等于语义正确。
7. 结果只读展示，支持取消并废弃旧上下文的晚到响应；不处理图片、价格库存、规则学习或上架提交。
