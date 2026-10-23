# AM 开发计划

状态：2026-10-21 建立。设计依据是 D:/ys-project/auto-magic-design，界面以 19-ui-design.md 为准。实现中发现与设计不符，先和用户讨论，再改文档。

## 工作方式

- 先实现模块，在实现中发现问题，再讨论、再设定、再解决。不要在动手前把所有细节设计完。
- 顺序由用户定：界面，然后接入采集插件，然后验证采集。提交、AI、规则引擎在这之后。
- 设计文档很大，不要整目录读入。先读 INDEX.md，需要哪份再单独读。
- 提交 git 前先问用户。只暂存明确的文件。

## 仓库现状

- 位置 D:/ys-project/AM，独立 git 仓库，没有远程，只在本机。设计目录 D:/ys-project/auto-magic-design 不是 git 仓库。
- 解决方案 AM.slnx：
  - src/AM.Desktop：WPF 界面（net10.0-windows，WPF-UI、CommunityToolkit.Mvvm）。
  - src/AM.Core：定价等纯逻辑。tests/AM.Core.Tests：xUnit。
  - src/AM.Bridge：插件协议。src/AM.NativeHost：Chrome native messaging 中转。
  - plugin/：Chrome 插件（加载已解压的扩展程序）。scripts/：NativeHost 注册。
- 启动：`dotnet run --project D:\ys-project\AM\src\AM.Desktop`。AM 运行时占用 bin 下的文件，构建会失败；验证可以输出到临时目录（`-o`）。
- 本机数据在 %LocalAppData%\AM\：settings.json（Ozon 凭据，DPAPI 加密）、category-tree.json、probes\、batches\、native-host.log。都不进仓库。
- 旧仓库 D:/ys-project/auto-magic 只作参考。用户不喜欢旧界面，不要复制或模仿它。

## 里程碑

### M0 骨架

- 构建通过。主窗口：顶部任务下拉框和设置图标，下面五个页签（采集、待处理、文案、提交、任务记录），页签带状态和数量。
- 空白页状态：只显示任务下拉框、"新建任务"、"最近未结束任务"提示（19 号"打开工具"一节）。
- 页签灰态规则：采集完成前待处理、文案、提交不可用；待处理清零后提交才可用。

### M1 界面（假数据）

用内存里的示例数据把每一页做出来，不接真实数据。范围见 19 号。（2026-10-22 接入真实采集后，示例批次、任务记录、统计数字和定价示例值已全部移除，内存存储改为 BatchStore。）

- 新建任务表单（含公式反推采购价区间显示）。
- 采集页：进度、商品列表、停止按钮和二次确认。
- 待处理页四个分段，源值行、缩略图、放弃、重新生成。
- 文案页、提交页（含提交确认弹窗）、任务记录页。
- 全局设置对话框的四组。OSS 一组暂不做，用户未定服务商。

开始前必须先问用户：界面风格。用户明确不喜欢旧界面，但没说过喜欢什么。不要自己猜。

### M2 数据层

- SQLite，参照 09-final-ddl.sql，并按 19 号末尾冲突清单调整：tasks.status 增加采集中和已中断，增加定价参数、汇率与浮动、人工审核模式、默认包装，新增 task_events 表。
- 此时才改 09 号文档，改之前先和用户确认。

### M3 接入采集插件

- 插件重写，不复制旧仓库 plugin/ 的代码。旧插件只用来查 1688 页面结构（内嵌 JSON、选择器、SKU 结构），并避开 14 号审计列出的问题。
- C# 端：AM.Bridge（新协议 collect.*，含停止消息）、AM.NativeHost（来自旧仓库，只改类型名）、桌面端 BridgeServer 和 CollectionService。
- 分两步：
  1. 通信骨架（已完成，2026-10-21 用户在真实 Chrome 和桌面端联调通过）：plugin/ 只有 fake-collector.js 产出假商品，验证两遍抓取、健康检查跳过第 2 遍、停止。注册用 scripts/register-native-host.ps1。
  2. 页面探测（已完成，2026-10-22）：plugin/probe.js 只在用户点插件按钮时读取当前页，结果经 probe.result 存到 %LocalAppData%\AM\probes\。结论写入设计目录 16 号"实测结果"一节；采购价口径（原价第一档）写入 13 号第 6 项。接完真实采集后 probe.js、popup、probe.result 一起删除。
  3. 真实采集（已完成，2026-10-22）：plugin/collector.js（流程）和 page-readers.js（页面读取）替换了 fake-collector.js。拼搜索 URL、跳过广告卡片、详情页读 window.context，CollectItemPayload 带 CollectedOffer。详情图只存 detailUrl，不抓图片（用户已同意）。价格排序因 URL 参数未实测，暂从界面移除。首次真实运行已完成（2026-10-22，5/5 成功，结果见设计目录 16 号）。批次数据写到 %LocalAppData%\AM\batches\（开发用，M2 后删除）。
- 采集规则：每商品最多抓两遍；第一遍结束后做一次健康检查，超阈值则跳过第二遍；数据在采集结束后一次性返回（16 号、19 号）。

### M4 验证采集

对照 16 号和 19 号"待验证"一节：

- 页面里有没有按 SKU 的重量和尺寸，覆盖率多少。
- SKU 价格、库存、图片的真实命中率。
- 搜索页列表价的含义（11 号清单）。
- 缩略图地址会不会过期。
- 停止消息能否让插件及时退出。

结果写回设计文档。

### 定价（已完成，2026-10-23 用户用真实批次确认）

- 公式和保护条件在 src/AM.Core/Pricing（PricingEngine、PricingParameters），tests/AM.Core.Tests 覆盖。规则见设计目录 13 号（方案 A）。
- 新建采集填定价参数并冻结；搜索按采购价上限（priceEnd）和选填的采购价下限（priceStart）粗筛；采集完成后逐 SKU 定价（Desktop/Services/BatchPricing），低于采购价下限的 SKU 丢弃。
- 结果：提交页显示保留 SKU 的价格列；价格丢弃写任务记录；价格异常显示在待处理页并阻断提交页。
- 采购价下限（选填，2026-10-23 用户要求）：用户实测不设下限时低价货质量差。
- 未做：待处理页"放弃该商品"，所以有价格异常的批次目前无法进入提交页。

## 下一步候选（顺序由用户定，新会话先问用户）

- 待处理页"放弃该商品"：解除价格异常对提交页的阻断。
- M2 数据层（SQLite）：现在批次、商品、任务记录只在内存里，关闭 AM 就丢失。
- 设置页"采集"一组：抓取间隔（现为占位 1.5 秒）、健康检查失败率阈值（现为占位 0.3）。
- 字典与匹配引擎、规则（18 号）。
- AI 文案和图片（12 号）、OSS（服务商未定）。
- 提交与状态轮询（10、11 号）。

已完成的：Ozon 设置与类目树缓存、真实采集、定价。

## 之后的模块（顺序未定）

字典、匹配引擎与规则、AI 文案和图片、OSS、提交与状态轮询。提交模块的第一次真实调用要先验证 11 号清单第 1 项（相同 offer_id 重提是否为更新），而且会向用户的真实卖家后台发请求，动手前必须取得用户明确同意。

## 未决事项

- OSS 服务商和字段（用户后续补充）。
- 汇率偏差阈值 3% 只是占位。
- 旧 AGENTS.md 的"规则变更必须单独汇报"是否沿用，未定。AM 的 AGENTS.md 暂未带入。
- 12 号文档引用的 04-data-mapping-strategy.md 在设计目录里找不到。
- 其他设计文档与 19 号的冲突（见 19 号末尾清单）尚未修改，等用户确认后再逐份改。

## 大文件与安全

- ozon-category-tree.test.json 的任何副本不要整读，只用 jq 或带 -m、head 的 grep。
- 不读 bin/、obj/ 构建输出。
- 密钥不进仓库、不进日志、不回显。
