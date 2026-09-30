# Auto Magic

更新：2026-09-30。Windows WPF / .NET 10 桌面应用，通过 Chrome 扩展与 Native Messaging Host 采集 1688 商品，使用人工定义的 JSON 字段规则生成待确认方案。

## 当前可用流程

1. 采集商品，选择详情商品查看源事实与数据清洗 JSON。
2. 选择 Ozon 品类和类型。开发期间自动读取本地 Schema 缓存；首次点击“请求并刷新 Schema”。
3. 点击“生成初步方案”。当前版本只生成 Schema 标记为必填的属性；没有映射规则的必填项仍显示为空并等待人工填写。商品公共字段显示一次，SKU 按来源值分组。
4. 修改中文内容、勾选已确认、导出确认 JSON。该阶段不查询 valueId，也不自动回写规则。

当前界面入口为 FieldBindingPreview。RuleReviewService、JsonRuleReviewStore 的字典核验、回写与统计服务仍保留，尚未接入新的分阶段确认流程；旧字典表单隐藏。导出不是完整上架请求，正式商品发布未实现。不使用 AI 推断缺失字段。

## 开发与测试

```powershell
dotnet build AutoMagic.slnx
dotnet run --project verification/AutoMagic.RuleReview.Checks
.\scripts\publish-dev.ps1
```

固定发布目录为 artifacts/desktop 和 artifacts/native-host。更新前退出桌面程序，避免文件占用。Chrome 扩展安装见 [插件说明](plugin/README.md)。首次联调需使用 scripts/register-native-host.ps1 的 ExtensionId 参数注册当前扩展 ID。

采集数据保存在 %LOCALAPPDATA%/AutoMagic/collections，Schema 缓存在同级 schema-cache。API Key 保存在 Windows Credential Manager。规则目录在仓库内默认为 rules，也可用 AUTO_MAGIC_RULES_DIRECTORY 指定；不要随发布清理删除采集数据或 reviews.db。

## 文档入口

- [开发规范](AGENTS.md)
- [字段规则、条件、默认值与值规则](rules/README.md)
- [采集实现](docs/collector/am-collector-logic.md)
- [数据清洗](docs/data-organization/cleaned-product.md)
- [尺码研究参考，非运行规则](docs/field-matching/size-reference.md)
- [Ozon 测试品类树](src/AutoMagic.Desktop/Data/README.md)

安装器、自动更新、完整上架和新确认流程的 valueId 解析仍未实现。当前不处理非必填属性。成本筛选使用 ExchangeRate-API 参考汇率；旧中国银行汇率说明已撤销。
