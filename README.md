# Auto Magic

更新：2026-10-15。Windows WPF / .NET 10 桌面应用，目标是从 1688 批量采集商品，按 Ozon 类目 Schema 清洗与匹配，组装提交 JSON 并通过 Ozon API 上架和查询状态。当前已实现采集、Schema 查看和规则清洗，完整方案见 [上架流程设计](docs/pipeline-design.md)。

## 当前可用流程

应用当前仅执行 [clean：数据清洗](rules/clean/README.md)。[match：字典匹配](rules/match/README.md)的规则 JSON 原样保留供后续开发，应用不读取或执行这些规则。

1. 采集商品，在“数据清洗”页选择详情商品查看源事实。
2. 在“引擎设置”选择 Ozon 品类 200000933、类型 93182；清洗无需 API 凭据或请求 Schema。
3. 点击“执行数据清洗”，“数据清洗 JSON”默认以表格并列显示源字段、源值、清洗值与结果来源，兜底和留空项单独标色。数组按属性集中展示，SKU 关系保留。
4. 人工补充 rules/clean 对应的规则 JSON 后，再次执行即可复测，无需重启。
5. 勾选“查看完整 JSON”或点击“导出清洗 JSON”查看、保存完整结果。另一个标签“源数据事实”仅显示源字段与原始值，便于核对。
6. 点击“铺满窗口”隐藏顶部搜索、成本区域和主导航；点击“退出放大”恢复布局。窗口缩小时，表格内部滚动。

类型清单声明属性 ID、中文说明 name 和路径；name 仅作说明，不参与匹配或覆盖模块。JsonProductCleaningRuleStore 从 clean 的公共、类目入口解析显式引用，展开完整规则，再由 ProductCleaningService 执行提取、转换与兜底。不自动继承或搜索其他规则，不合并多层默认值；修改模块后重新执行即可生效。ProductCleaningInputBuilder 只读取源事实、规格与 SKU，不组装目标 Schema 或生成派生候选。数据清洗页仅保留清洗结果和源数据事实；旧匹配、人工确认、SKU 身份计划及引擎预览实现已移除。Schema 查看独立保留，刷新 Schema 不影响当前清洗结果。采集文件、规则文件和凭据保留。导出不是完整上架请求，正式商品发布未实现。不使用 AI 推断缺失字段。

## 开发与测试

```powershell
dotnet build AutoMagic.slnx
.\scripts\publish-dev.ps1
```

固定发布目录为 artifacts/desktop 和 artifacts/native-host。更新前退出桌面程序，避免文件占用。dotnet build 只更新构建目录，不会更新常用客户端；修改界面后还需发布并重新打开 artifacts/desktop/AutoMagic.Desktop.exe。当前没有自动化检查项目，清洗行为通过桌面“执行数据清洗”人工复测。Chrome 扩展安装见 [插件说明](plugin/README.md)。首次联调需使用 scripts/register-native-host.ps1 的 ExtensionId 参数注册当前扩展 ID。

采集数据保存在 %LOCALAPPDATA%/AutoMagic/collections，Schema 缓存在同级 schema-cache。API Key 保存在 Windows Credential Manager。规则目录在仓库内默认为 rules，也可用 AUTO_MAGIC_RULES_DIRECTORY 指定；不要随发布清理删除采集数据。

## 文档入口

- [开发规范](AGENTS.md)
- [字段规则、条件、默认值与值规则](rules/README.md)
- [上架流程设计：已确认方案与待核实事项](docs/pipeline-design.md)
- [采集实现](docs/collector/am-collector-logic.md)
- [尺码研究参考，非运行规则](docs/field-matching/size-reference.md)
- [Ozon 测试品类树](src/AutoMagic.Desktop/Data/README.md)

安装器、自动更新、完整上架仍未实现。清洗处理 JSON 显式声明的全部属性，不按 Schema 必填标记过滤，也不代表已完成官方在线核验。成本筛选使用 ExchangeRate-API 参考汇率；旧中国银行汇率说明已撤销。
