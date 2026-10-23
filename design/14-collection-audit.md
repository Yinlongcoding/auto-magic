# 现有 1688 采集审计（子代理报告摘要）

来源：只读审计子代理的报告，我没有逐行复核。未读完 detail-extractor.js 部分行段、structured-sku-reader.js 大部分、background.js 大部分、popup.js；桌面端只检查了部分文件。以下均为"该报告称"。

## 采集方式

- 应用下发任务（search.start：keyword、maxItems≤60、采购价上下限、排序、是否抓详情事实）。
- 插件复用或新建 1688 搜索页，应用价格与排序，滚动加载，再用一个共享标签页串行抓详情。
- 通信：Chrome native messaging，经 NativeHost 进程走命名管道到桌面端（不是 HTTP）。
- 另有 chrome.debugger 附加标签页、MAIN world 注入读取页面内嵌 JSON。
- 当前只处理前 10 条详情（测试上限）。

## 已采集字段

- 商品级：offerId（URL 正则）、标题（document.title 清洗）、属性表（label/value 扁平列表）。
- SKU 级：规格轴（名称、选项、选项图）、组合（skuId、价格、库存、图片）。仅当页面有可识别的内嵌 JSON 才有。
- 图片：raw.imageUrls 最多 100 张，主图与详情图混在一个数组，不区分类型。

## 下游真正使用的

只有属性表（label/value）、标题、offerId、SKU 规格组合的选项。SKU 价格、库存、图片、skuId 等均已存储但未被使用。

## 缺口

- 描述文本：没有，只有详情图 URL。
- 品牌：无专门抓取，仅当品牌恰好在属性表里才出现。
- 主图与详情图：未区分。
- SKU 价格、库存、图片：代码支持，但真实命中率未确认（无运行数据）。
- 批发价阶梯、起订量：未采集。

## 臃肿点与风险

- 标题存 4 处；协议里有 DetailSnapshotDto 与 DetailResults 两套结构，前者疑似无人产出。
- normalizedValue 恒等于 sourceValue，status 恒为 observed。
- priceTexts、skuTexts、diagnostics 无下游读取。
- 选择器脆弱：列表页和详情页依赖具体类名，主图硬编码丢弃最后一项，详情图选择器过宽可能混入无关图。
- 笛卡尔推导出的 SKU 价格库存为空，下游不检查就当作真实 SKU。
- background.js 1797 行，抽象是否过重未确认。

## 对新设计的影响

1. 搜索任务协议已携带采购价上下限，说明采集前价格区间筛选在协议层面可行（见 13-pricing.md）。插件是否真的在搜索页生效、能否精确过滤，未实测。
2. 12-ai-integration.md 的 Prompt 输入写了"源描述"，但采集没有描述文本。需决定去掉它，或新增采集。
3. SKU 价格逐 SKU 筛选（13 号文档）依赖 SKU 价格，而它的命中率未知，需要实测。
4. 笛卡尔推导的 SKU 没有价格，不能参与价格筛选，需明确处理（阻断或剔除）。
