# Auto Magic 项目交接文档

更新日期：2026-08-26
当前阶段：1688 普通 Chrome 数据采集技术验证
最低目标系统：Windows 10

## 1. 一句话状态

Auto Magic 已从“通用 RPA 执行器优先”调整为“Ozon 选品与自动上架优先”。1688 官方 API 已被排除；当前技术路线是通过 Chrome MV3 扩展和 `chrome.debugger` 控制用户正在使用的普通 Chrome，完成1688搜索、DOM读取和结构化JSON输出。扩展第一阶段 Demo 已实现；最新修改包含复用当前窗口已有搜索结果页、按搜索框文字决定是否重新搜索、模拟鼠标滚轮分段触发懒加载，以及按新商品卡片DOM提取链接、图片、纯文字名称和价格，但这些修改仍需在真实 Chrome 中验收。

## 2. 产品目标与版本边界

### 当前版本目标

面向普通用户发布一个 Windows 桌面应用，完成以下主流程：

1. 用户在 Auto Magic 中输入选品条件或关键词。
2. Auto Magic 控制普通 Chrome 打开1688并搜索商品。
3. 从公开页面DOM读取商品列表与后续商品详情。
4. 将商品信息转换为稳定的内部数据结构。
5. 后端统一调用百炼模型完成文本翻译、润色和图片翻译。
6. 图片通过对象存储短期中转，默认有效期三天。
7. 桌面客户端使用用户自行录入的 Ozon Key 调用 Ozon API 完成上架。

### 已延期到 2.0 的能力

- 面向无API平台的通用RPA脚本执行器。
- 通用元素拾取器。
- 用户自定义自动化脚本市场或脚本编辑器。

### 已明确排除

- 1688 官方开放平台 API：申请主体和解决方案资质不适合当前实施条件。
- 独立 Playwright 浏览器作为正式采集底座：可用于开发验证，但新会话稳定性不足。
- 绕过登录、验证码、访问限制或其他安全控制。

## 3. 核心思考方向

项目决策遵循 Silver Thinking：安全优先于便利，长期可维护性优先于短期拼接，结论必须有运行数据支持。

### 为什么选择普通 Chrome 扩展

独立 Playwright Demo 曾成功读取一次1688“连衣裙”搜索页，得到5,932个DOM元素、407张图片、296个链接，并可从现有链接识别59个唯一商品ID；短时间内第二次运行却跳转登录页。两次样本不足以证明稳定性，但已证明DOM数据可提取，同时暴露出新浏览器会话不稳定的问题。

普通 Chrome 扩展路线可以复用用户的真实浏览器会话、Cookie、缓存和日常环境，减少每次创建新浏览器上下文带来的波动。它仍不保证永远不会触发平台验证，因此后续必须测量成功率，而不是把“理论上更稳定”写成既定事实。

### 为什么使用 Node.js / TypeScript 方向

- 用户熟悉前端与 Node.js 生态。
- Chrome 扩展、桌面应用和数据协议可以共享类型与校验逻辑。
- 不需要为了DOM解析引入Python运行时。
- 后续 Native Host 若性能压力不大，可继续使用 Node.js；正式打包时再评估独立可执行文件或 Rust 小型桥接程序。

### 为什么不做元素拾取器

当前由开发者主动维护 CSS 选择器。选择器集中版本化，页面改版时只修改解析层，不把元素选择能力扩展成通用RPA产品。

## 4. 已确认的总体架构

```text
Auto Magic 桌面应用
    │
    │ Windows Named Pipe（待实现）
    ▼
Auto Magic Browser Host（待实现）
    │
    │ Chrome Native Messaging（待实现）
    ▼
Chrome MV3 扩展（第一阶段 Demo 已实现）
    │
    │ chrome.debugger / CDP + chrome.scripting
    ▼
用户当前使用的普通 Chrome
    │
    ├─ 创建1688首页标签页
    ├─ 输入关键词并按 Enter
    ├─ 追踪1688新开的结果标签页
    ├─ 等待商品卡片稳定
    └─ 提取结构化JSON
    │
    ▼
Auto Magic 桌面应用
    ├─ 后端：百炼 + OSS
    └─ 客户端：用户自己的 Ozon Key → Ozon API
```

Native Messaging 连接必须由扩展发起。正式链路应由扩展调用 `chrome.runtime.connectNative()`，Chrome 启动 Browser Host；Browser Host 再通过 Windows Named Pipe 与正在运行的 Auto Magic 通信。不要把 Native Host 误设计成桌面应用可以直接调用扩展的通道。

## 5. 1688采集任务状态机

当前确认的用户流程：

1. Auto Magic 创建带唯一 `jobId` 的采集任务。
2. 如果 Chrome 未运行，Auto Magic 正常启动 Chrome，不使用远程调试端口。
3. 扩展查询当前 Chrome 窗口，优先选择已有的1688搜索结果页；只有不存在结果页时才创建 `https://www.1688.com/` 标签页。
4. 扩展通过 `chrome.debugger.attach()` 附加目标标签页；Chrome显示“Auto Magic 正在调试您的浏览器”。
5. 等待 `#alisearch-input` 出现，读取搜索框当前 `value`，去除首尾空白后与目标关键词比较。
6. 关键词一致时不修改搜索框、不发送 Enter，直接使用当前结果；关键词不一致时聚焦并全选搜索框，使用 CDP `Input.insertText` 替换关键词。
7. 需要重新搜索时，在触发搜索前注册 `chrome.tabs.onCreated` 和 `chrome.tabs.onUpdated` 监听。
8. 使用 CDP `Input.dispatchKeyEvent` 发送 Enter 的 `rawKeyDown` 与 `keyUp`。
9. 通过 `openerTabId`、1688域名和任务时间窗口识别新结果标签页；同时保留当前页跳转兼容分支。
10. 等待页面加载完成，通过 CDP `Input.dispatchMouseEvent(type: "mouseWheel")` 每次向下滚动200px、间隔250ms，直至页面底部。
11. 到达底部后等待250ms，再以每次600px、间隔250ms向上滚动，直至 `scrollY <= 4` 的页面顶端。
12. 回到顶端后注入固定商品解析器，生成结构化JSON。
13. 将结果返回桌面端。
14. 成功、失败或取消时解除所有调试连接并移除监听器。

不要依赖动态搜索按钮类名。2026-08-26 的1688首页确认搜索框为 `#alisearch-input`，表单使用 `target="_blank"`；当前实现已经从“找按钮点击”改为“聚焦输入框后按Enter”。

## 6. 当前JSON结构

任务请求示例：

```json
{
  "type": "START_1688_DOM_DEMO",
  "keyword": "连衣裙",
  "maxItems": 60
}
```

结果示例：

```json
{
  "type": "PRODUCT_SEARCH_RESULT",
  "jobId": "uuid",
  "success": true,
  "data": {
    "keyword": "连衣裙",
    "sourceUrl": "https://s.1688.com/...",
    "pageTitle": "...",
    "capturedAt": "2026-08-26T00:00:00.000Z",
    "count": 59,
    "items": [
      {
        "detailUrl": "https://detail.1688.com/offer/897255629209.html",
        "imageUrl": "https://...",
        "title": "商品标题",
        "priceCny": "43.80"
      }
    ]
  }
}
```

当前只传结构化字段，不默认传完整HTML或图片二进制。图片阶段只传URL，后续由受控服务下载、校验并上传OSS。

## 7. AI、OSS 与 Ozon 决策

### 百炼模型

- 文本翻译与润色：`Qwen-mt-plus`。
- 图片翻译：`Qwen-mt-image`。
- 百炼调用统一放在后端，桌面端不内置百炼长期密钥。
- 具体模型可用区、价格、请求结构和输出格式在正式接入前仍需重新核对当前官方文档。

### 对象存储

- 作为图片处理过程中的临时中转。
- 默认有效期三天。
- 应使用生命周期规则和短期签名URL，不公开永久地址。
- 不在 Native Messaging 中传输大图片二进制。

### Ozon

- 用户在客户端自行录入店铺 Key。
- 后端不保存 Ozon Key。
- Ozon API 由客户端调用。
- Windows端建议用 DPAPI 或 Windows Credential Manager 保存密钥，不使用明文配置文件。

## 8. 应用行为与UI历史基线

应用名称为 Auto Magic，版本号和Logo未定，Windows 10为最低支持系统。早期已确认1440×900桌面设计方向：固定左侧导航、底部账户入口、主内容卡片、运行日志和设置页面。

但这些界面来自“通用RPA执行器”阶段。业务已经转向Ozon选品与上架，因此旧设计只能作为视觉风格参考，不能直接当作当前信息架构最终稿。下一轮产品设计应重新定义“选品任务、商品处理、上架记录、店铺设置”等导航与页面。

关闭 Auto Magic 时，如果仍有任务执行：

1. 弹出确认提示。
2. 用户仍选择关闭，则停止任务。
3. 不记录当前进度。
4. 下次从头开始。

## 9. 当前代码与目录

```text
plugin/
├─ manifest.json              MV3权限与入口
├─ background.js              标签页、CDP、任务状态机
├─ content/extractor.js       1688商品DOM解析
├─ lib/config.js              URL校验、选择器、限额
├─ popup.html/js/css          手工启动与JSON预览
└─ README.md                  扩展安装与验证步骤

test/
└─ chrome-extension-extractor.test.mjs  商品DOM解析回归测试
```

Node.js要求20或更高版本。务必在项目根目录运行命令，避免再次出现从 `C:\Users\Administrator\src\...` 错误路径启动脚本的问题。

## 10. 已完成与证据

### 已完成

- Playwright读取JavaScript渲染后DOM的通用Demo。
- 中文关键词GBK编码测试，“连衣裙”对应 `%C1%AC%D2%C2%C8%B9`。
- 1688搜索页DOM、图片、文本、链接的实际读取验证。
- Chrome MV3扩展骨架。
- `chrome.debugger`附加与CDP输入。
- 新标签页跟踪逻辑。
- 商品列表滚动到底部并等待懒加载稳定的逻辑。
- 基于 `.search-offer-item` 的商品DOM转JSON解析器。
- `.title-text` 使用 `textContent` 提取纯文字，保留子标签内文字但不输出HTML标签。
- 商品链接限定为1688 HTTPS地址，图片限定为HTTPS地址。
- 扩展Popup启动、状态和JSON复制。
- 1688域名白名单、关键词和数量校验。
- 搜索动作从动态按钮点击改为键盘Enter。

### 自动测试

2026-08-26 当前仓库使用随附 Node.js v24.19.0 执行扩展解析和搜索状态测试：6项通过，0项失败；插件与测试共6个JavaScript文件语法检查通过。测试覆盖四字段解析、纯文字标题、URL校验、关键词一致性比较、当前窗口结果页选择、无结果页分支，以及向下200px、向上600px、250ms间隔和底部暂停配置。旧电脑记录的 `npm test` 15项测试文件当前不在仓库中，不能视为本机可复现证据。

### 尚未完成真实浏览器验收

最新结果页复用、关键词比较、Enter、模拟鼠标滚轮懒加载和四字段解析方案尚未在重新加载后的普通 Chrome 扩展中完成端到端验证。旧版 `window.scrollTo` 直接跳底方案真实运行只得到9条商品，已经被运行结果否定并移除；新方案仍需真实验收。自动测试和语法检查不能证明实际页面交互成功，这是下次会话的第一优先事项。

## 11. 下次会话第一步

1. 打开 `chrome://extensions/`。
2. 找到 `Auto Magic 1688 DOM Demo` 并点击刷新。
3. 打开扩展，保留默认关键词“连衣裙”。
4. 点击“开始验证”。
5. 检查：
   - 是否创建1688首页标签页；
   - 是否显示Chrome调试提示；
   - 是否正确输入“连衣裙”；
   - Enter是否打开结果标签页；
   - 当前窗口已有且关键词一致时，是否复用原结果页并跳过输入和Enter；
   - 当前窗口已有但关键词不一致时，是否在原结果页替换文字后才执行Enter；
   - 页面是否按每次200px、间隔250ms的鼠标滚轮步进持续向下移动；
   - 到达底部后是否等待250ms，再按每次600px、间隔250ms向上移动；
   - 回到页面顶端后，是否才开始输出JSON；
   - 扩展徽标是否显示商品数量；
   - 重开Popup后是否能看到JSON；
   - 每条商品是否包含 `detailUrl`、`imageUrl`、纯文字 `title` 和 `priceCny`。

如果失败，先读取扩展 Service Worker 的实际错误和页面最终URL，不要直接叠加备用点击逻辑。特别检查：输入框是否仍然获得焦点、Enter是否触发站点监听器、新标签页是否具有正确的 `openerTabId`。

## 12. 当前阻塞与已知风险

- Enter触发尚未真实验收。
- Native Messaging Host和Named Pipe尚未实现。
- MV3 Service Worker重启后的任务恢复策略未实现；当前任务仅存在内存中。
- 任务取消、用户关闭任务标签页、调试器被DevTools替换等异常路径需要补测。
- CSS选择器会随1688改版变化，应加入版本和失败快照。
- 普通Chrome会话预期比独立浏览器稳定，但缺少足够样本；需要记录成功、登录跳转、验证码和超时比例。
- 正式发布前需要确认1688页面数据使用、服务条款、频率和隐私边界。
- Chrome扩展正式分发优先考虑Chrome Web Store；普通用户手工导入CRX的安装体验和更新能力不应被假定为稳定方案。

## 13. 安全与维护约束

- `host_permissions`仅允许1688域名。
- 不允许桌面端下发任意JavaScript；使用固定命令协议。
- 所有来自页面、内容脚本和Native Host的数据都必须校验。
- Native Host的 `allowed_origins`必须固定为正式扩展ID，禁止通配符。
- 仅控制Auto Magic创建或明确授权的标签页。
- 任务结束必须解除Debugger。
- 日志不记录Cookie、Authorization、Ozon Key或百炼密钥。
- 正式版本应对任务JSON建立版本化Schema。

## 14. 推荐实施顺序

1. **验证Enter搜索和列表JSON。**
2. 修复实际DOM解析字段，建立至少10条商品的人工对照样本。
3. 增加取消、超时、标签页关闭与Debugger断开处理。
4. 实现Native Messaging Host最小Demo。
5. 使用Windows Named Pipe连接Auto Magic主进程。
6. 定义版本化任务/结果Schema。
7. 采集成功率与异常类型数据。
8. 接入商品详情页采集。
9. 接入百炼、OSS和Ozon API。
10. 回到桌面端UI与安装发布流程。

## 15. 下次会话可直接使用的开场说明

> 继续 Auto Magic 项目。请先阅读 `docs/AUTO_MAGIC_HANDOFF_2026-08-26.md`。当前第一优先级是验证 `chrome-extension-demo` 最新的 Enter 搜索方案；不要恢复1688官方API，也不要把独立Playwright作为正式采集底座。请根据真实Chrome运行结果继续修正，并保持Silver Thinking的安全、证据和可维护性优先原则。
