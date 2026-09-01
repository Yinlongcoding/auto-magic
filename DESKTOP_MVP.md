# Auto Magic 桌面端 MVP

当前骨架完成了第一条端到端链路：

`WPF 搜索框 -> Named Pipe -> Native Messaging Host -> Chrome 插件 -> 1688 DOM 抓取 -> JSON -> WPF`

当前桌面端还包含一组筛选测试表单：

- 自动读取中国银行外汇牌价的现汇卖出价，并显示 CNY→USD、CNY→RUB 参考值和发布时间；
- 输入平台佣金比例，以及物流、广告和其他 CNY 固定成本，数字输入支持小数；
- 通过目标利润率滑块设置利润目标，默认 10%，不改变用户输入的售价；
- 输入 CNY 售价区间后，动态显示目标利润以及扣除目标利润和各项成本后的可采购成本区间；
- 商品列表按“最低售价仍达到目标利润”进行保守筛选，不额外展示利润汇总或选中商品利润。

## 本地构建

构建脚本优先使用仓库内的 `.tools/dotnet`，若该目录不存在则使用系统安装的 .NET 10 SDK。换机后可直接执行：

```powershell
dotnet build .\AutoMagic.slnx
dotnet test .\AutoMagic.slnx
```

## 首次联调

如果 Windows PowerShell 提示系统禁止运行脚本，可先在当前窗口执行：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
```

该设置仅对当前 PowerShell 进程生效，关闭窗口后恢复原策略。

1. 发布桌面端和原生消息宿主：

   ```powershell
   .\scripts\publish-dev.ps1
   ```

2. 打开 `chrome://extensions`，启用开发者模式，选择“加载已解压的扩展程序”，目录为项目下的 `plugin`。
3. 复制 Chrome 显示的 32 位扩展 ID。
4. 为当前 Windows 用户注册原生消息宿主：

   ```powershell
   .\scripts\register-native-host.ps1 -ExtensionId <扩展ID>
   ```

5. 完全重载扩展，或重启 Chrome。
6. 先运行 `artifacts\desktop\AutoMagic.Desktop.exe`，等待界面显示“Chrome 插件已连接”。
7. 输入关键词并点击“搜索并抓取”。插件会复用当前窗口已有的 1688 搜索结果页；关键词不一致时会替换并回车搜索。完成懒加载滚动后，商品表格和原始 JSON 会显示在桌面端。

## 当前限制

- 一次只允许一个搜索任务。
- 桌面端搜索会返回商品列表，并通过真实 Chrome 渲染页采集列表第 2 条商品的详情事实，供 Ozon 属性覆盖与 AI 语义映射测试使用。
- 桌面端必须先启动。若桌面端未运行，Chrome 原生消息宿主会退出，扩展稍后自动重连。
- 开发阶段需手动提供扩展 ID 注册宿主；正式安装包会自动完成该步骤。
