# WPF 桌面端指南 (`Yuandl.ModbusAssistant`)

本文档说明 **C# .NET 8 WPF 桌面工作站** 的 UI 主题集成、协议栈边界、安全性限制、配置生命周期及构建打包方式。

## 1. UI 主题体系 (`Yuandl.ThemeUI 2.0.7`)

- 项目通过 NuGet 显式锁定引用 `Yuandl.ThemeUI` (`2.0.7`)，并在 `App.xaml` 中合并：
  - `ThemesDictionary`（默认 `Light`，运行时支持切换 `Dark`）
  - `ControlsDictionary`
  - 应用工作站色板扩展字典（`Themes/LightTheme.xaml`、`Themes/DarkTheme.xaml`）
- 主窗口采用 `Yuandl.ThemeUI.Controls.FluentWindow` 与 `ui:TitleBar`，并在构造函数及主题切换时调用 `ApplicationThemeManager.Apply(...)` 保持主题一致性。
- **启动自检 (`App.xaml.cs`)**：应用启动时会执行 `EnsureThemeUiLoaded()` 验证 `Yuandl.ThemeUI` 程序集与核心资源字典是否成功加载；若缺失或损坏会立即记录致命日志并弹窗提示终止，避免静默退化运行。

## 2. 协议栈与 `HslCommunication` 边界设计

当前 WPF 与 Web 共用统一的 Modbus 语义标准（参见 [协议规范](protocol.md)）：

- **自研核心协议栈作为唯一事实来源**：
  - 主站帧构建/匹配：`ModbusFrame`、`CommunicationService`
  - 内置从站服务：`ModbusServerService`（支持 `Tcp` / `SerialRtu` / `SerialAscii` / `TcpAndSerial`、在线改值、`UnitId=0` 广播静默执行）
- **为什么不混用两套 Modbus 从站/主站实现**：
  - 如果同时在 `ModbusServerService` 与 `HslCommunication` 之间双写或双服务端运行，会导致功能码异常码（如 `0x02`/`0x03`/`0x0B`）、四字序规则（尤其是 64 位 `WordSwap`）与寄存器越界检查出现分裂。
  - 项目在 `Yuandl.ModbusAssistant/Protocol/Modbus/IModbusClientAdapter.cs` 定义了可选适配器接口。如需对接特定 PLC 私有协议（如三菱 MC / 西门子 S7 / 欧姆龙 FINS），应通过独立适配器扩展，而保持 Modbus 主从核心栈统一。

## 3. 安全限制与通道并发模型

### 3.1 TCP Server 安全限制

无论无协议 `TcpServerChannel` 还是内置 Modbus TCP 从站 `ModbusServerService`，均支持可配置防护参数：

| 参数 | 默认值 | 说明 |
| :--- | :--- | :--- |
| `BindAddress` | `127.0.0.1` | 默认绑定本机回环；可选择 `0.0.0.0`（全网卡）或指定本机 IPv4 网卡 |
| `MaxClients` | `32` | 超过上限的新连接在 `Accept` 后记录告警并立即关闭 |
| `IdleTimeoutMs` | `60000` | 客户端无有效收发活动的空闲超时断开时长（`0` 表示禁用） |
| `MaxFrameLength` | `4096` / `260` | 无协议通道上限 4096 B（可配）；Modbus TCP 从站限制单帧总长及缓冲区膨胀 |
| `SendQueueLimit` | `64` | 每个客户端会话独立的有界发送队列，防止慢客户端拖死广播发送 |
| `AllowedClientIps` | 空（不限） | 可选逗号分隔 IP 白名单（如 `127.0.0.1,192.168.1.10`），仅放行匹配远端 IP |

### 3.2 通道事务调度与后台生命周期

- `CommunicationService` 对 **串口 / RTU / ASCII / UDP** 保持严格的单请求-单响应互斥串行调度（半双工总线约束）；对 **Modbus TCP 客户端通道** 采用带超时和 `TransactionId` 关联的通道级事务队列，并在断线/取消/关闭时确定性清理挂起请求与记录原因日志。
- `AsyncRelayCommand` 提供返回 `Task` 的 `ExecuteAsync` 与集中异常日志捕获，避免 `async void` 吞掉异常或导致进程崩溃。
- `RawDebugViewModel` 定时发送与 `RegisterMonitorViewModel` 采集循环全部使用可取消的 `PeriodicTimer` + `CancellationTokenSource` 异步循环，在页面停止或窗口销毁时可靠退出。

## 4. 版本化配置模型与点表生命周期

- `PersistenceService` 采用版本化工作区模型（`WorkspaceConfiguration` v2，`.tmp` 原子替换落盘），启动时自动兼容迁移旧版分立配置（`settings.json`、`profiles.json`、`server-config.json`、`monitor-config.json`）。
- 服务器点表与设备方案管理支持 **编辑暂存 -> 显式「应用点表 / 保存变更」或「撤销未应用更改」**，避免表单微调或导入中途误写正在运行的从站内存。
- CSV 点表导入严格校验：最大文件大小（5 MB）、UTF-8 BOM 与 RFC-4180 引号转义、地址与类型宽度范围、数值可解析性及地址重叠检查。

## 5. 构建、测试与发布

```powershell
# 1. 还原、编译并运行 xUnit 测试（含协议向量、TCP 安全策略、重连与 ThemeUI 启动验证）
dotnet restore .\Yuandl.ModbusAssistant.sln
dotnet build .\Yuandl.ModbusAssistant.sln --configuration Release --no-restore
dotnet test .\Yuandl.ModbusAssistant.sln --configuration Release --no-build --verbosity normal

# 2. 若需生成离线恢复锁定文件（仅需在有网络的 Windows/SDK 环境执行一次）
dotnet restore .\Yuandl.ModbusAssistant.sln --use-lock-file

# 3. 发布免安装目录及 Inno Setup 安装包
.\packaging\publish.ps1
.\packaging\publish.ps1 -BuildInstaller
```

> 运行日志按日写入 `%LocalAppData%\Yuandl.ModbusAssistant\logs`（保留 14 天），可在软件「系统设置」页一键打开日志目录。
