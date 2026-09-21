# Auto Magic 项目交接文档

> 交接日期：2026-09-21
>
> 工作目录：`D:\ys-project\auto-magic`
>
> 支持平台：Windows 10/11 x64
>
> Chrome 插件版本：`0.1.22`
> 当前阶段：第一阶段映射预览、分类规则框架和 Ozon 属性组合草稿已经可运行；正式上架尚未开放

## 1. 当前目标与边界

```text
桌面端输入商品关键词并选择 Ozon description_category_id/type_id
→ Chrome 插件复用当前 1688 登录会话获取商品列表
→ 按列表顺序串行采集详情事实、图片证据和真实 SKU
→ 读取所选 Ozon 类型的动态 Schema
→ 分类规则优先、AI 补缺，形成可审计的属性建议
→ 本地校验事实引用、字典、类型、基数、必填项和 SKU 作用域
→ 每个真实 SKU 组合为一个 Ozon items[] 草稿
→ 后续补齐商业字段后批量提交并读取上架结果
```

当前只实现到“可审阅的 Ozon 属性组合草稿”。图片处理、最终价格库存、重量尺寸、税率、商品创建/更新 API 和上架结果读取尚未实现。`ReadyForListing` 与 `ReadyForSubmit` 必须保持 false，不能把预览结果直接提交 Ozon。

Ozon 品类和类型由用户选择，AI 不负责猜测品类。程序会对少量高置信度冲突进行前置阻断，例如所选类型为“无袖连衣裙”、源事实却是“袖长：长袖”。

## 2. 重要工作区状态

当前工作树包含一整轮尚未提交的架构替换和功能开发，存在大量修改、删除和新增文件。**不要执行 `git reset --hard`、`git clean` 或按旧版本恢复被删除文件。** 被删除的第一代映射代码是计划内清理，不是误删。

仓库根目录的 [AGENTS.md](AGENTS.md) 规定：任何映射规则、SKU 有效性规则、分组策略、转换规则或优先级发生变化，都必须在同一任务的最终回复中单独汇报规则名称、作用范围、变更前后行为和验证结果。

当前最新桌面构建：

```text
artifacts/desktop-phase1-v2/AutoMagic.Desktop.exe
```

旧的 `artifacts/desktop-phase1/AutoMagic.Desktop.exe` 在最后一次发布时被正在运行的旧进程占用，因此最新构建输出到了 `desktop-phase1-v2`。

## 3. 技术基线

- .NET 10、WPF、CommunityToolkit.Mvvm。
- `win-x64` self-contained 单文件桌面发布。
- Chrome MV3 扩展复用用户当前 Chrome 登录态。
- Chrome Native Messaging → `AutoMagic.NativeHost` → 当前用户 Named Pipe → WPF。
- Ozon 和百炼密钥保存在 Windows Credential Manager，不进入插件、Native Host、日志或 Git。
- Ozon Schema 和字典由桌面端直连官方 Seller API。
- Qwen 使用 DashScope OpenAI 兼容接口、严格 JSON Schema、非思考模式和固定低随机性。
- 当前为轻量分层单体；SQLite、规则 revision、云端同步和 Velopack 尚未接入。

## 4. 关键文件

```text
plugin/content/detail-extractor.js           详情 DOM、事实、图片和结构化 SKU 整理
plugin/lib/structured-sku-reader.js           页面上下文 SKU 探针
plugin/lib/detail-fact-filter.js              语义表格矩阵污染过滤
plugin/background.js                          搜索、标签页、详情串行队列和缓存

src/AutoMagic.Application/Ozon/Mapping/
  ProductMappingContracts.cs                  ProductMapping V2.1 合同
  ProductMappingInputBuilder.cs               原始事实、真实 SKU 和目标 Schema 输入构造
  QwenProductMappingTransport.cs              模型侧压缩合同与 eN/vN 别名恢复
  ProductMappingRunner.cs                     规则优先、AI、字典查询和最多两轮调用
  ProductMappingResponseNormalizer.cs         仅机械归一化，不判断商品语义
  ProductMappingValidator.cs                  确定性合同验证
  ProductVariantIdentityFactory.cs            稳定 variantKey/merchantSku
  CategoryRuleMatchingEngine.cs               分类规则匹配框架
  OzonFieldCompositionEngine.cs               Ozon items[] 属性组合草稿

src/AutoMagic.Infrastructure/Ozon/Mapping/
  QwenProductSemanticMapper.cs                百炼调用适配器

docs/ai-mapping/phase1-product-mapping.md      第一阶段行为与边界
docs/category-rules/README.md                  分类规则分层与学习流程
docs/collector/am-collector-logic.md           采集器事实来源
```

## 5. 已完成的清理

以下第一代实现已移除：

- AttributeCoverageResolver；
- FieldMatchingEnginePlan / FieldMatchingFinalOutput；
- SemanticMapping V1 合同、Prompt、Schema、服务和验证器；
- 全局 `RussianSizeRuleCatalog`；
- `ConversionRuleSelector`；
- 旧 Qwen V1 界面页签和对应测试；
- 服装专用颜色/尺码归一化模块；
- 颜色×尺码 DOM 笛卡尔式后备逻辑。

可复用的 Qwen 运行参数和 JSON 配置已迁移到 `AiMappingRuntime.cs`。当前有效入口是 ProductMapping V2.1，不要重新引用已删除的 V1 类。

## 6. Chrome 采集器现状

### 列表和详情流程

- 复用当前 Chrome 登录会话和搜索结果页。
- 最多保存 60 条列表记录。
- 测试阶段只串行采集前 10 条详情。
- 同时只使用一个共享详情标签页。
- 首次读取当前 DOM；属性或 SKU 不完整时只滚动目标区域，不做全页滚动。
- 单商品总预算 30 秒；失败进入二次采集队列。
- 详情缓存键为 `offerId + cacheVersion`，当前缓存版本为 `v11`。

### SKU 原则

- 只接受页面真实结构化数据中存在的组合；
- 保留 `9号`、`均码`、`大码`、`36键` 等原始值；
- 真实但库存为零的 SKU 保留为 `stock: 0 / unavailable`；
- 不构造页面没有提供的笛卡尔积；
- 有 SKU 文本或规格轴、但没有真实组合时，详情状态为 `partial`。

SKU 探针目前覆盖：常见和动态命名的页面全局商品状态、React 节点状态、`data-*` JSON、静态 JSON、`JSON.parse(...)` 初始化内容、`skuBase + skuCore` 及 `skuProps + skuInfoMap` 等结构。诊断会输出 `inspectedRootPaths`、`selectedSourcePath`、`selectedShape`、维度数和组合数。

### 事实污染过滤

`detail-fact-filter.js` 会识别已经有完整规格集合的“颜色/规格 × 重量或尺寸”矩阵。例如：

```text
颜色：粉红色,藏青色,黑色
颜色：重量(g)
粉红色：380
藏青色：380
黑色：380
```

后四项不会再作为普通商品事实。没有矩阵表头证据时，数字字段不会被随意删除。

## 7. ProductMapping V2.1

内部请求保留请求身份、采集批次、商品身份、品类/type、原始事实和路径、真实 SKU、稳定 `variantKey/merchantSku` 及完整 Schema。

模型只接收压缩合同：品类、`字段：值`事实、逐变体事实、目标属性和字典候选。证据使用短别名 `eN`，变体使用 `vN`。模型若只改变别名大小写，例如把 `e5` 返回成 `E5`，程序会在唯一匹配时机械恢复；未知别名仍按 `evidence.fabricated` 拒绝。

第一轮 AI 为必填属性返回建议或明确未解决状态。字典字段第一轮只提供查询文本，程序随后调用 Ozon 字典搜索；取得真实候选时最多追加一次 AI 调用。第二轮只能选择程序提供的 ID/文本组合。

验证器检查请求身份、属性 ID、证据与 SKU 作用域、SKU 集合、字典候选、数据类型、基数、复杂属性实例和逐 SKU 必填覆盖。AI 输出不能直接成为 Ozon 请求。

## 8. 分类规则匹配引擎

当前规则目录为 `CategoryRuleCatalog.Empty`：框架可运行，但没有内置任何真实服装、电子或其他品类业务规则。AI 结果不会自动写入规则库。

规则分三层：

1. `CommonSemanticModule`：只定义语义键和来源标签别名，不包含 Ozon ID 或具体商品值。
2. `CategoryCommon`：绑定一个 `description_category_id`，把语义键连接到实际 Ozon 属性。
3. `TypeOverride`：必须声明明确 `type_id`，只保存相对品类通用规则的例外。

同一属性和作用域冲突时，`TypeOverride` 覆盖 `CategoryCommon`。确定性直接映射覆盖 AI；字典待解析规则允许 AI 在官方候选中完成选择。目录加载时会拒绝层级声明错误、重复 ID 和无效属性绑定。

当前“学习”仍是人工流程：真实样本发现重复关系 → 人工审核 → 代码增加候选规则 → 多样本回归。尚未实现候选规则自动生成、审核 UI、持久化、revision、成熟度提升和回滚。

## 9. Ozon 字段组合引擎

- 一个真实 1688 SKU 生成一个 Ozon `items[]` 元素；
- 商品公共属性复制到每个 item；
- SKU 映射覆盖同属性/复杂实例键的公共值；
- 复杂属性按 `complexInstanceKey` 分组；
- 映射失败或必填项未解决时不生成可提交请求；
- 当前始终加入 `composition.commercial_fields_deferred`。

多个 SKU 在 API 层仍是多个 offer。若要在 Ozon 前台合并成一张卡片，必须由相应品类规则填写该 Schema 支持的分组属性一致值，不能把多个 SKU 塞进一个 item。

## 10. 最近真实样本与修复

最近样本是 1688 offer `1069577638062`，标题为“法式立体花朵灯笼袖棉麻连衣裙…”。该轮暴露并修复了：

1. “无袖连衣裙”与“袖长：长袖”冲突：增加 `category.type_conflict`，在 AI 调用前阻断。
2. 页面有颜色/尺码文本但没有结构化组合：扩大 SKU 探针数据来源，仍需新版插件真实复测。
3. 重量矩阵被拆成普通事实：增加矩阵过滤器。
4. 模型把 `e5` 返回为 `E5`，造成 13 个伪造证据错误：支持唯一的大小写机械恢复。

该样本的材质数据本身冲突，适合作为 `ambiguous` 回归样本，不应学习成材质规则。

## 11. 界面查看方式

在“Ozon Schema”选择类型并读取 Schema，完成新版采集后，到“字段匹配”选择商品并运行 AI。重点查看：

- AI 映射建议；
- SKU 身份计划；
- 校验与未解决问题；
- 源商品事实；
- AI 输入 JSON；
- AI 原始响应；
- 引擎结果 JSON。

“引擎结果 JSON”包含：

```text
mapping            规则与 AI 合并后的映射、校验和调用记录
categoryRules      本次规则命中、证据和缺口
ozonImportDraft    逐 SKU 的 Ozon 属性草稿和阻断项
```

## 12. 凭证和本地数据

| 数据 | 保存位置 |
| --- | --- |
| Ozon API Key | Windows Credential Manager：`AutoMagic.Ozon.TestApiKey` |
| 百炼 API Key | Windows Credential Manager：`AutoMagic.Qwen.DashScopeApiKey` |
| Ozon Client ID、已选品类/类型 | `%LOCALAPPDATA%\AutoMagic\ozon-test-settings.json` |
| Chrome Native Host manifest | `%LOCALAPPDATA%\AutoMagic\NativeMessaging\com.automagic.desktop.json` |
| Native Host 注册表 | `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.automagic.desktop` |

禁止把真实凭证写入源码、Markdown、测试数据、日志或提交信息。

## 13. 构建和测试

```powershell
dotnet build AutoMagic.slnx --no-restore
dotnet test AutoMagic.slnx --no-restore --verbosity minimal

$testFiles = Get-ChildItem -LiteralPath 'test' -Filter '*.test.mjs' |
  ForEach-Object { $_.FullName }
node --test $testFiles
```

2026-09-21 最近基线：

- `AutoMagic.Contracts.Tests`：82 通过；
- `AutoMagic.Desktop.Tests`：8 通过；
- Chrome 插件 Node 测试：32 通过；
- JavaScript 语法检查通过；
- `git diff --check` 无内容错误，只有 Windows 行尾提示；
- `win-x64` self-contained 发布成功。

发布命令：

```powershell
dotnet publish src/AutoMagic.Desktop/AutoMagic.Desktop.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts/desktop-phase1-v2 `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

## 14. 新环境恢复

1. 安装 Git、Chrome 和 .NET 10 SDK。
2. 构建或执行 `scripts/publish-dev.ps1`。
3. 在 `chrome://extensions` 加载仓库的 `plugin` 目录。
4. 复制扩展 ID并执行：

   ```powershell
   .\scripts\register-native-host.ps1 -ExtensionId <扩展ID>
   ```

5. 重新加载扩展，确认版本为 `0.1.22`。
6. 先启动桌面端，再执行搜索采集。
7. 重新录入 Ozon Client ID、Ozon API Key 和百炼 API Key。

## 15. 下一步优先级

### P0：真实复测本轮采集修复

1. 重新加载插件 `0.1.22`；缓存 `v11` 会避开旧结果。
2. 用 offer `1069577638062` 或同类商品重新采集。
3. 确认重量矩阵污染消失。
4. 检查 `raw.skuDimensions`、`raw.skuCombinations` 和 `diagnostics.skuMatrix`。
5. 若仍无组合，保存 `inspectedRootPaths`、`selectedSourcePath`、`selectedShape` 和新源 JSON，为真实页面增加结构适配；不要改成笛卡尔积。
6. 选择与长袖事实一致的 Ozon 类型，再运行 AI。
7. 确认不再出现仅由 `E5/e5` 造成的 `evidence.fabricated`，并验证字典第二轮是否触发。

### P1：规则学习闭环

- 定义候选规则和 `draft/candidate/stable/revalidation_required` revision 合同；
- 增加人工审核、启用、禁用和回滚界面；
- 保存样本数、供应商覆盖、成功/失败计数和 Schema 指纹；
- 只有多样本验证后才能从 type 补丁提升为品类通用规则；
- 只有值变化时更新 ValueMappingCache，不重写字段关系；
- 每次规则变化必须遵守 `AGENTS.md` 的汇报要求。

### P2：Ozon 可提交合同

- 图片选择与处理；
- 标题/描述生成与人工复核；
- 价格、库存、重量、尺寸、币种和税率；
- 品类分组属性策略；
- Ozon 批量提交、任务轮询、逐 offer 错误归因和结果展示；
- 幂等键、失败重试和审计记录。

### P3：持久化和发布

- SQLite 与 migration；
- 规则 revision、ValueMappingCache 和同步 Outbox；
- 用户/租户隔离与云端乐观并发；
- Velopack 安装、签名和自动更新。

## 16. 不得回退的约束

- 不把 Ozon 品类选择交给 AI。
- 不把 AI 输出直接提交 Ozon。
- 不允许模型编造字典 ID、事实、尺码换算或运营策略。
- 不使用全品类全局硬编码俄罗斯尺码规则。
- 不按颜色、尺码或其他规格轴构造未经页面确认的笛卡尔积。
- 不因为规格值是“9号、均码、大码、36键”就从采集层删除。
- 不把“其他”自动解释为“无品牌”。
- 不把商品级事实错误应用到 SKU，或把一个 SKU 的事实串到另一个 SKU。
- 不恢复已删除的 SemanticMapping V1 和第一代字段计划 UI。
- 修改插件运行逻辑时必须递增并报告 manifest 版本。
- 真实密钥和浏览器凭据不得进入 Git、日志或模型输入。

## 17. 接手验收清单

- 仓库可以构建并通过 .NET 与 Node 测试；
- 插件显示 `0.1.22`，Native Host 与桌面端连接正常；
- 1688 搜索可以得到列表并串行采集前 10 条详情；
- 无真实 SKU 组合时结果为 `partial`，不会伪造 SKU；
- Ozon Schema 能按所选品类/type 读取；
- 类型明显冲突时 AI 不会被调用；
- 正常样本能获得严格 Qwen 响应并通过或明确失败于本地验证；
- “引擎结果 JSON”可以查看规则命中、校验结果和 Ozon 属性草稿。
