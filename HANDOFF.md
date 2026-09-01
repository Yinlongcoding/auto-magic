# Auto Magic 项目交接文档

> 交接日期：2026-09-02
> 当前分支：`main`
> 支持平台：Windows 10/11 x64
> Chrome 插件版本：`v0.1.10`
> 当前阶段：1688 列表/详情采集、Ozon 动态 Schema、Qwen 语义映射候选链路已接通；尚未进入最终 Ozon 发布

## 1. 换机后先看这里

本仓库根目录在当前电脑是 `D:\Project\auto-magic`，但代码和脚本不依赖该固定路径。换机后以新克隆目录为准。

- `plugin/` 就是最新 Chrome 插件目录。
- `.tools/`、`artifacts/`、`bin/`、`obj/` 被 Git 忽略，不会随提交迁移。
- Ozon API Key 和百炼 API Key 保存在当前 Windows 用户的 Credential Manager，不会进入 Git，也不会自动迁移到另一台电脑。
- Chrome 扩展 ID 可能随电脑或加载方式变化，换机后必须使用新 ID 重新注册 Native Messaging Host。
- 本文档是当前项目进度的首要交接入口；架构基线见 `DESKTOP_FOUNDATION.md`，AI 映射合同见 `docs/ai-mapping/qwen-runtime-contract.md`。

## 2. 当前产品目标

Auto Magic 当前要实现的是 Windows 桌面端驱动的跨境商品自动化链路：

```text
用户在桌面端选定 Ozon 品类/类型并填写成本参数
  -> 桌面端计算 1688 可采购价格区间
  -> Chrome 插件复用用户当前登录态搜索 1688
  -> 应用价格与排序条件并完整加载列表
  -> 返回列表 JSON，并采集第 2 条商品的详情事实
  -> 桌面端读取 Ozon 当前品类/类型的动态属性 Schema
  -> Qwen 根据详情事实和 Ozon 属性生成可审计的语义映射候选
  -> 本地验证器校验结构、证据和状态一致性
  -> 后续由确定性代码解析字典、执行转换与策略并编译 Ozon 提交参数
```

重要边界：Ozon 品类和类型在采集 1688 商品前由用户选定，不需要 AI 猜测品类。AI 只参与动态字段语义匹配，不能直接发布商品，也不能自行编造字典值、转换规则或运营策略。

## 3. 已冻结的桌面端技术决策

- Windows 10/11 x64，C# / .NET 10，WPF。
- 桌面端发布为 `win-x64` self-contained 单文件。
- 用户使用插件前必须主动启动桌面应用；插件不得静默拉起桌面端。
- Chrome 扩展通过 Native Messaging 连接 `AutoMagic.NativeHost.exe`，Native Host 再通过当前用户专用 Named Pipe 连接桌面端。
- Native Host 只做协议转发，不读取 Ozon/百炼凭证，不访问数据库。
- 官网或私有渠道分发；当前阶段保持闭源并暂不做 Authenticode 公共签名。
- 自动更新目标是启动时检查、后台下载、任务空闲后提示安装；Velopack 尚未实际接入。
- 详细安全边界和发布原则以 `DESKTOP_FOUNDATION.md` 为准。

## 4. 仓库结构

```text
plugin/                              Chrome MV3 插件 v0.1.10
scripts/                             发布、注册/注销 Native Host 脚本
src/AutoMagic.Desktop/               WPF 界面与 ViewModel
src/AutoMagic.NativeHost/            Chrome stdio <-> Named Pipe 转发
src/AutoMagic.Contracts/             IPC DTO 与协议
src/AutoMagic.Application/           用例接口、Ozon/映射合同
src/AutoMagic.Domain/                定价与商品领域逻辑
src/AutoMagic.Infrastructure/        Bridge、汇率、Ozon、Credential Manager、Qwen
tests/AutoMagic.Contracts.Tests/     .NET 自动化测试
test/                                Chrome 插件 Node 测试
docs/ai-mapping/                     Qwen Skill、Prompt、Schema、样本和运行合同
```

## 5. Chrome 插件当前能力

当前实际版本为 `v0.1.10`。修改 `plugin/` 内运行逻辑或界面时，必须递增 `plugin/manifest.json` 版本，并在交付说明中写明版本号。

### 5.1 列表搜索和筛选顺序

1. 优先复用当前 Chrome 窗口已有的 1688 搜索结果页；没有才创建页面。
2. 读取 `#alisearch-input` 并与目标关键词比较。相同则复用，不同才替换文本并按 Enter。
3. 进入列表时先保持页面顶部，不做首次懒加载滚动。
4. 填写最小采购价。
5. 用真实鼠标事件点击最大采购价输入框并填写最大采购价，持续保持焦点，使隐藏的“确定”按钮可见。
6. 在同一价格表单中锁定父级为 `.price-filter-define`、文字为“确定”的 `.sn-common-button-dpl-define`，阻止 `mousedown` 默认失焦后再完成点击。
7. 以 URL 的 `priceStart` / `priceEnd` 判断筛选是否真正生效。
8. 再应用排序：默认销量优先；价格优先沿用 `.sw-dpl-orderby-asc` 与 `bottom-active` 判断。
9. 排序刷新后，从顶部开始每次向下滚动至少 600px，间隔 250ms，直到页面底部。
10. 到底后不额外等待，立即抓取 `.search-offer-item` 商品集合，最多 60 条。
11. 返回列表 JSON 后把页面移回顶部。

列表字段：详情链接、`.main-img` 图片、`.title-text` 的纯 `textContent` 名称、`.text-main` 价格。对广告卡片若没有静态详情链接，会真实点击商品图片区解析新开的详情页，再关闭临时标签。

“连衣裙”曾作为验收关键词，正常应得到 60 条；容器已从旧的 `.search-offer-wrapper` 改为 `.search-offer-item`。不要恢复旧选择器或重新加入首轮滚动。

### 5.2 详情采集

- 桌面端流程默认采集列表第 2 条商品。
- 使用真实 Chrome 渲染页在非激活标签中等待动态属性区域，采集通用标签/值事实，完成后关闭临时标签。
- 详情事实是动态键值集合，不是只针对连衣裙写死的字段映射；上限目前为 500 条事实。
- 展示图读取 `.od-picture-gallery-list > .v-image-cover` 的内联 `background-image`，排除最后一个“参数”元素并按最终 URL 去重。
- 插件弹窗另有基于后台 `fetch()` 的原始 HTML/DOM 快照调试功能；该功能不执行页面 JavaScript，不等同于桌面端真实渲染详情采集。

更完整的行为、诊断字段和选择器说明见 `plugin/README.md`。

## 6. 桌面端当前能力

### 6.1 搜索、汇率和反推采购成本

- 搜索关键词由桌面端传给插件，最终在桌面端显示商品列表和原始 JSON。
- 汇率以中国银行外汇牌价为基准，显示 CNY -> USD / RUB 参考值。
- 用户输入售价区间、平台佣金比例、物流费用、广告推广、其他固定 CNY 成本和目标利润率。
- 应用不以 1688 商品价格计算利润；它先根据用户售价和成本目标反推出允许的 1688 采购成本区间，再把该区间用于 1688 筛选。
- 数字输入支持小数。

### 6.2 Ozon 动态 Schema

- 本地测试品类树位于 `src/AutoMagic.Desktop/Data/ozon-category-tree.test.json`。
- 当前快照为中文版：26 个顶级目录、568 个品类节点、7,365 个商品类型；仅用于流程验证，不是永久生产目录。
- UI 中品类与类型 ID 均为选择式。
- 使用 Ozon `Client-Id` 和 `Api-Key` 请求选定品类/类型的动态属性，并生成属性覆盖报告。
- 当前属性 Schema 接口 `/v1/description-category/attribute` 一次返回完整 `result`，实现没有伪造分页参数；字典值接口的分页属于下一阶段。具体实现与测试见 `OzonSchemaService` 和 `OzonSchemaServiceTests`。

### 6.3 Qwen 语义映射

已实现从界面主动发起的第一版真实 API 调用链路：

- 服务：阿里云百炼 DashScope，华北 2（北京）。
- Base URL：`https://dashscope.aliyuncs.com/compatible-mode/v1/`。
- 固定模型：`qwen3.7-plus-2026-05-26`。
- 非思考模式、最低随机性、严格 JSON Schema；不启用联网、工具调用或外部检索。
- 输入由 Ozon 属性表、1688 详情事实、运行上下文和版本信息组成。
- 系统约束来自 `docs/ai-mapping/qwen-system-prompt.txt` 与 `docs/ai-mapping/SKILL.md`。
- 输出先经严格反序列化，再由本地验证器检查属性顺序、原始证据、未映射事实补集、状态依赖与字典 ID 白名单。
- 当前通过校验的结果只在 UI 展示，不写入映射方案库，也不会提交到 Ozon。

当前 Skill 已明确：`其它` 不等于“无品牌”；“无吊牌/无领标”不能作为品牌证据；`图片色` 是占位值；策略、转换和字典待解析状态必须一致。样本与预期见 `docs/ai-mapping/qwen-evaluation-guide-1060627180703.md`。

## 7. 本地数据与凭证

以下内容不会通过 Git 迁移，换机后需要重新录入：

| 数据 | 保存位置 |
| --- | --- |
| Ozon API Key | Windows Credential Manager：`AutoMagic.Ozon.TestApiKey` |
| 百炼 API Key | Windows Credential Manager：`AutoMagic.Qwen.DashScopeApiKey` |
| Ozon Client ID、已选品类/类型 | `%LOCALAPPDATA%\AutoMagic\ozon-test-settings.json` |
| Chrome Native Host manifest | `%LOCALAPPDATA%\AutoMagic\NativeMessaging\com.automagic.desktop.json` |
| Native Host 注册表 | `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.automagic.desktop` |

禁止把真实 Key 粘贴进源码、Markdown、JSON 样本、测试、日志或提交信息。

## 8. 新电脑恢复步骤

### 8.1 前置条件

- Windows 10/11 x64。
- Git、Chrome。
- .NET 10 SDK。`global.json` 当前要求 `10.0.400` 并允许 `latestPatch`。
- PowerShell 在仓库根目录运行。

### 8.2 获取代码并构建

```powershell
git clone <仓库地址>
cd <新仓库目录>
dotnet --version
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\publish-dev.ps1
```

`publish-dev.ps1` 会优先使用 `.tools\dotnet\dotnet.exe`；若它不存在，则自动使用系统 `dotnet`。输出位于：

- `artifacts\desktop\AutoMagic.Desktop.exe`
- `artifacts\native-host\AutoMagic.NativeHost.exe`

### 8.3 加载插件并注册桥接

1. 打开 `chrome://extensions`，启用开发者模式。
2. 选择“加载已解压的扩展程序”，目录选 `<仓库根目录>\plugin`。
3. 复制 Chrome 显示的新扩展 ID。
4. 在仓库根目录执行：

   ```powershell
   .\scripts\register-native-host.ps1 -ExtensionId <扩展ID>
   ```

5. 在 Chrome 中重新加载插件，必要时重启 Chrome。
6. 先启动 `artifacts\desktop\AutoMagic.Desktop.exe`，再使用插件或桌面搜索。
7. 在桌面端重新录入 Ozon Client ID、Ozon API Key 和百炼 API Key。

如需清理旧注册：

```powershell
.\scripts\unregister-native-host.ps1
```

## 9. 测试基线

交接前最近一次完整自动化测试结果：

- .NET：35 通过，0 失败。
- Chrome 插件 Node 测试：22 通过，0 失败。
- 合计：57 通过，0 失败。
- Release 桌面构建和 `win-x64` self-contained 发布成功。
- 本次交接环境无法连接 NuGet 漏洞数据源，发布时出现 `NU1900` 审计警告；包恢复与编译仍成功。换机联网后应重新发布并确认漏洞审计结果。

换机后建议先运行：

```powershell
dotnet test .\AutoMagic.slnx
node --test .\test\*.test.mjs
```

若本机 Node 不支持 PowerShell 通配符，可逐个运行 `test/` 下的 `*.test.mjs`，或在支持 glob 的 shell 中执行。

## 10. 当前尚未完成

1. 尚未使用用户真实百炼 Key 完成 UI 端到端调用验收；当前服务有 fake handler 单元测试和严格校验测试。
2. 尚未实现 Ozon 属性字典值接口、分页缓存和候选值缩窄。字典字段现在只能停在 `dictionary_pending`。
3. 尚未实现俄罗斯尺码等确定性转换规则。
4. 尚未冻结“合并至一张卡片”等运营策略。
5. 尚未将通过校验的映射编译为最终 Ozon 发布参数。
6. 尚未实现品类映射方案本地版本化、云端备份和“同品类新版本覆盖旧备份”。
7. 尚未调用 Ozon 最终商品创建/更新接口。
8. Velopack 安装、自动更新和更新签名仍是技术基线，尚未实现。
9. 最新 Qwen UI 变更发布后尚需一次人工视觉检查和真实数据回归。

## 11. 推荐下一步

第一优先级是完成“真实 Qwen 映射闭环验证”，不要直接开始 Ozon 发布：

1. 在新电脑发布并启动最新桌面端，确认插件显示 `v0.1.10`。
2. 重新录入 Ozon 与百炼临时凭证。
3. 选择测试品类/类型，读取 Ozon Schema。
4. 搜索一次 1688 商品并确认返回第 2 条详情事实。
5. 点击“执行AI映射”，保存请求 JSON、原始响应、验证结果和 Token 用量用于评审；不要保存 API Key。
6. 确认真实调用稳定后，实现 Ozon `/v1/description-category/attribute/values` 字典分页、缓存和候选缩窄。
7. 将字典候选交给 Qwen 或本地匹配器，再由确定性编译器输出最终属性值。
8. 最后设计映射方案版本、品类级复用和云端替换式备份。

## 12. 不得回退的验收约束

- 不能把 Ozon 品类识别重新交给 AI；品类/类型由用户事先选定。
- 不能为每个品类硬编码一套详情字段；详情采集必须保留动态事实模型。
- 不能把列表页 JSON 当成详情页 JSON。
- 不能把 Qwen 输出直接当作 Ozon 发布参数。
- 不能让模型编造 Ozon `valueId`、尺码换算或业务策略。
- 不能把 `其它` 自动认定为“无品牌”。
- 不能在列表筛选前先做完整懒加载滚动。
- 不能恢复 `.search-offer-wrapper`；当前列表容器是 `.search-offer-item`。
- 修改插件时必须递增并报告版本号。
- 任何真实密钥均不得进入 Git。

## 13. 交接完成标准

换机成功的最小门禁：

- 仓库可在新路径构建、测试和发布。
- 插件从 `plugin/` 加载后显示 `v0.1.10`。
- 使用新扩展 ID 注册 Native Host 后，桌面端能显示插件已连接。
- “连衣裙”搜索能完成价格筛选、排序、60 条列表加载及第 2 条详情事实采集。
- Ozon Schema 能按已选品类/类型读取。
- 录入百炼 Key 后能获得 Qwen 严格 JSON 响应，并通过或明确失败于本地验证器。
