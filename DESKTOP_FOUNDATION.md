# Auto Magic Windows 桌面端技术基线

> 状态：已冻结，可进入项目骨架实施
> 决策日期：2026-08-28
> 适用范围：Auto Magic Windows 桌面端、Native Messaging Host、安装与自动更新
> 变更规则：涉及安全边界、进程关系、安装范围、更新策略或数据所有权的变更，必须先更新本文档和 `megic.md`

## 1. 目标与非目标

### 1.1 目标

- 为 Chrome 扩展提供稳定、可升级的 Windows 桌面底座。
- 管理商品采集任务、商品数据、详情数据、日志和后续 Ozon 上架流程。
- 使用用户当前 Chrome 会话，不启动新的浏览器环境。
- 在不要求管理员权限的前提下完成安装、Native Messaging 注册和自动更新。
- 将浏览器协议、本机进程通信、数据库和长期凭据划分为明确的安全边界。

### 1.2 非目标

- 不支持 macOS、Linux、Windows 7/8 或 ARM64 首发包。
- 不提供低代码流程设计器或任意脚本下发能力。
- 首版不实现关闭到系统托盘。
- 首版不自动上传日志、崩溃信息或使用行为遥测。
- 当前开发和封闭测试阶段不购买或接入公共 Authenticode 证书。

## 2. 平台与发布运行时

| 项目 | 决策 |
| --- | --- |
| 操作系统 | Windows 10/11 |
| CPU | 首版仅 x64 |
| 语言与运行时 | C# / .NET 10 LTS |
| UI | WPF |
| 发布模式 | `win-x64` Self-contained |
| MVVM | CommunityToolkit.Mvvm，不引入 Prism |
| 应用宿主 | .NET Generic Host |
| 安装范围 | 当前用户级，不要求管理员权限 |
| 分发渠道 | 官网或私有渠道直接下载安装包 |
| 安装与更新 | Velopack |

Self-contained 构建必须包含发布时选定的 .NET 10 安全补丁。运行时出现安全更新后，需要重新构建并发布桌面端版本。

## 3. 解决方案结构与依赖边界

建议的生产项目：

```text
AutoMagic.Desktop          WPF、View、ViewModel、应用入口
AutoMagic.NativeHost       Chrome stdio 与 Named Pipe 转发
AutoMagic.Contracts        版本化 IPC DTO 和协议定义
AutoMagic.Application      任务用例、接口和业务编排
AutoMagic.Domain           领域实体和业务规则
AutoMagic.Infrastructure   EF Core、Credential Manager、HTTP、更新和日志
```

测试项目至少包括：

```text
AutoMagic.Tests.Unit
AutoMagic.Tests.Integration
AutoMagic.Tests.Architecture
```

依赖规则：

```text
AutoMagic.Desktop -> AutoMagic.Application -> AutoMagic.Domain
AutoMagic.Desktop -> AutoMagic.Infrastructure
AutoMagic.Desktop -> AutoMagic.Contracts

AutoMagic.Infrastructure -> AutoMagic.Application
AutoMagic.Infrastructure -> AutoMagic.Domain

AutoMagic.NativeHost -> AutoMagic.Contracts
```

强制约束：

- Native Host 不得引用 Infrastructure、Application 或数据库项目。
- Domain 不得引用 WPF、EF Core、HTTP、Velopack 或 Windows API。
- ViewModel 不直接创建数据库连接、HTTP Client 或 Credential Manager 句柄。
- 所有外部输入在进入 Application 用例前完成结构和边界校验。

## 4. 进程与生命周期

```text
Chrome 扩展
    <-> Chrome Native Messaging：stdin/stdout
AutoMagic.NativeHost.exe
    <-> Named Pipe：CurrentUserOnly
AutoMagic.Desktop.exe
```

### 4.1 Desktop

- WPF 主程序保持单实例。
- 用户使用插件前必须主动启动桌面端。
- 应用启动后建立 Named Pipe Server，并对外报告协议版本和就绪状态。
- 点击关闭即退出；有活动任务时必须二次确认。
- 用户确认退出后停止任务、关闭 Pipe、释放数据库与日志，再结束进程。
- 首版不默认开机启动，不关闭到托盘。

### 4.2 Native Host

- 由 Chrome 按 Native Messaging 规则启动。
- 不显示 UI，不访问 SQLite，不读取 Credential Manager。
- 桌面端未运行时立即返回 `DESKTOP_NOT_RUNNING`，不自动启动桌面端。
- `stdout` 只能输出 Native Messaging 帧；诊断信息只能写入 `stderr` 或独立日志。
- Chrome 连接关闭后及时退出，不作为常驻后台服务。

## 5. IPC 协议

### 5.1 Chrome 与 Native Host

- 遵循 Chrome Native Messaging 的长度前缀 UTF-8 JSON 协议。
- Native Host manifest 的 `allowed_origins` 固定为正式扩展 ID，禁止通配符。
- 只接受固定命令白名单，不允许任意 JavaScript、命令行或文件系统指令。

### 5.2 Native Host 与 Desktop

- 使用 Windows Named Pipe。
- Server 和 Client 均使用 `PipeOptions.CurrentUserOnly`。
- 使用固定长度头加 UTF-8 JSON 消息体，处理半包、粘包和断线。
- 消息信封至少包含：`protocolVersion`、`requestId`、`type`、`payload`、`error`。
- 所有请求必须设置大小上限、超时和取消策略。
- 拒绝未知协议版本、未知命令、重复请求 ID、非法 JSON 和超限消息。
- 协议 DTO 使用 `System.Text.Json`，Schema 和兼容策略纳入源码版本控制。

## 6. 数据与凭据

### 6.1 SQLite

- 使用 SQLite、EF Core 10 和 `Microsoft.EntityFrameworkCore.Sqlite`。
- 只有 Desktop 可以打开数据库；Native Host 和 Chrome 扩展不能直接访问。
- 使用 EF Migration 管理 Schema，所有 Migration 必须纳入源码并人工审查。
- 迁移前创建可恢复备份；迁移失败时不得覆盖原始数据库。
- 商品金额按最小货币单位整数保存，同时记录货币代码，避免浮点误差。
- 时间统一以 UTC 保存，在 UI 层转换为本地时区。
- JSON/CSV 只用于导入、导出和调试，不作为主数据库。
- 数据库、日志和配置必须存放在 Velopack `current` 目录之外。

### 6.2 Windows Credential Manager

- Ozon Key 使用 Windows Generic Credential 保存。
- Desktop 通过 `CredWriteW`、`CredReadW`、`CredDeleteW` 调用系统 API。
- 不需要管理员权限，不携带额外原生 DLL。
- Target Name 使用稳定命名空间，例如 `AutoMagic/Ozon/{shopId}/ApiKey`。
- 删除店铺配置时同步删除对应凭据。
- Key 不得进入 SQLite、配置文件、日志、异常、导出文件、Native Host 或 Chrome 扩展。
- Desktop 直接调用 Ozon API；后端不接收、不保存 Ozon Key。
- Credential Manager 不能抵御已取得同一 Windows 用户权限的恶意程序，不能将其描述为应用独占保险库。

## 7. 安装与 Native Messaging 注册

- 使用 Velopack 生成当前用户级 `Setup.exe` 和更新包。
- 程序安装到 `%LocalAppData%` 下的 Auto Magic 应用目录。
- Native Messaging Host 使用 HKCU 注册，不写 HKLM。
- 安装和升级生命周期钩子生成或更新 Native Host manifest，并写入 HKCU 注册项。
- 卸载生命周期钩子删除对应注册项和 manifest。
- Native Host manifest 和可执行文件路径使用稳定的 `current` 路径。
- 数据、日志和用户配置不得存放在会被更新替换的 `current` 目录。

## 8. 自动更新

目标流程：

```text
应用启动
-> 检查官网或私有更新源
-> 有新版本则后台下载
-> 等待任务空闲
-> 提示用户确认
-> 关闭 Native Host 连接并安全停止后台资源
-> 安装更新
-> 重启 Desktop
```

规则：

- 每次启动固定检查更新。
- 检查和下载不得阻塞主窗口启动。
- 活动任务期间禁止安装更新。
- 下载完成后不强制安装；必须等待空闲并获得用户确认。
- 使用 HTTPS 更新源。
- 更新清单必须由离线发布私钥签名；客户端只内置公钥。
- 安装前验证更新清单签名、目标版本、文件大小和 SHA-256。
- 拒绝签名错误、哈希不一致、版本降级、重复版本和不兼容渠道。
- Authenticode 延期不等于可以省略更新清单的独立密码学签名。

## 9. 日志与隐私

- 业务代码统一依赖 `Microsoft.Extensions.Logging`。
- 使用 Serilog 输出本地滚动结构化日志。
- Desktop 与 Native Host 写入不同文件，避免多进程争抢同一日志。
- 日志必须设置单文件大小、总保留量和保留时间限制。
- 不记录 Cookie、Authorization、Ozon Key、百炼密钥或完整敏感请求体。
- 首版不自动上传日志、崩溃信息或使用行为遥测。
- 用户可以主动导出经过脱敏的诊断包。

## 10. Authenticode 当前状态

- 项目保持闭源。
- 当前开发与封闭测试阶段暂不购买公共可信代码签名证书。
- 不要求普通用户安装自签名根证书。
- 无签名安装包可能触发浏览器下载警告、SmartScreen 或企业策略拦截。
- Windows 11 Smart App Control 可能直接阻止未知的无签名程序。
- 首次公开测试或正式公开发布前，必须基于目标用户安装成功率重新评估 Authenticode。
- 在未完成该重新评估前，不得宣称安装程序适用于所有 Windows 10/11 用户环境。

## 11. 最小验收门禁

### 11.1 安装与启动

- Windows 10/11 x64 普通用户无需管理员权限即可安装、启动和卸载。
- 未安装 .NET Runtime 的干净系统可以启动 Self-contained 应用。
- Native Messaging HKCU 注册在安装、升级和卸载后状态正确。
- Desktop 未运行时插件收到明确离线错误；启动后可以重新连接。

### 11.2 进程与协议

- Desktop 保持单实例。
- Native Host 崩溃不导致 Desktop 退出。
- 非当前用户或不同权限级别进程无法连接 Named Pipe。
- 半包、粘包、超限消息、非法 JSON、未知命令和超时均有确定结果。
- Native Host 的 `stdout` 不包含任何非协议输出。

### 11.3 数据与退出

- EF Migration 可从每个已发布 Schema 版本升级到最新版。
- 迁移失败后原数据库可恢复。
- 强制退出后数据库完整，任务不会永久停留在运行中。
- 有活动任务时关闭应用必须二次确认；取消关闭后任务继续执行。
- 应用升级后数据库、配置、日志和 Credential Manager 凭据不丢失。

### 11.4 更新安全

- 被替换或篡改的更新清单和更新包必须拒绝安装。
- 活动任务期间不得应用更新。
- 用户拒绝更新后应用继续正常运行，并可在后续再次提示。
- 无签名安装包必须在 Windows 10/11 SmartScreen 和 Smart App Control 环境完成真实记录。

## 12. 延后事项

- Windows ARM64 安装包。
- “关闭到系统托盘”设置项。
- 自动日志或崩溃信息上传。
- 公共 Authenticode 代码签名。
- Microsoft Store 分发。
- UI 主题与第三方控件库选型。

以上延后事项不得在未更新本文档的情况下隐式加入首版范围。
