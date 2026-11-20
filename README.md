# Yuandl Modbus Assistant

Yuandl Modbus Assistant 是一个面向工业通信调试与设备联调的开源工具项目，包含 **C# .NET 8 WPF 桌面工作站** 与 **React + TypeScript + Express Web 版**。支持串口物理/虚拟仿真、TCP/UDP 客户端与多客户端服务端、Modbus RTU/TCP/ASCII 调试（含 `0x0F`/`0x10` 批量写入与站号 `0` 广播写入）、内置多模式 Modbus 从站（支持在线改值与 CSV 点表导入导出）、多通道实时示波器以及 RTT 报文分析。

## 核心功能

- **多通道通讯连接**：支持物理串口（`System.IO.Ports`）、虚拟环回仿真串口（可显式开关回退策略）、TCP Client（含断线自动重连）、UDP、无协议 TCP Server（多客户端会话列表、按会话定向发送与强制踢线）。
- **内置 Modbus 从站服务器**：支持 `Tcp`、`SerialRtu`、`SerialAscii`、`TcpAndSerial` 双通道模式，支持 `UnitId=0` 广播写入静默执行、TCP 主站接入会话监控与断开、在线改值（按 Enter/写入或布尔按钮即时生效）、JSON 配置与 **CSV 点表导入/导出**。
- **Modbus 主站调试 (FC 01–10)**：覆盖 `01/02/03/04` 读取、`05/06` 单写、`0F/10` 批量写入及 `UnitId=0` 广播写；支持事务号与功能码严格帧匹配及错帧重同步。
- **无协议收发与报文分析**：支持 HEX/ASCII 编解码、定界符追加、发送历史、定时发送互斥保护、自动滚屏开关与 **CSV / TXT 日志导出**；RTT 往返时延采用有界采样窗口（2000 条）防长期运行内存膨胀。
- **数据监听 & 示波器**：支持多采集组轮询与从站内存实时监听、Bool 方波与 Float/Double 多字字节序（`BigEndian` / `LittleEndian` / `WordSwap` / `ByteSwap`）、边沿触发、冻结缩放及波形 CSV 导出。
- **设备管理一键联调**：支持配置集持久化，提供「载入」与「载入并连接」按钮一键填充端点参数、默认站号/协议格式并切换至连接页。
- **稳定性与可运维性**：原子化 JSON 配置文件写入（`.tmp` 替换防止断电损坏）、全局未处理异常捕获、按日滚动日志（`%LocalAppData%\Yuandl.ModbusAssistant\logs`，保留 14 天），设置页提供一键「打开日志目录」。

> **串口实现说明：** WPF 桌面版支持系统物理串口与可控的内联虚拟环回仿真串口；Web 版串口为虚拟仿真通道，不直接访问本机物理串口。

## 项目结构

```text
.
├── Yuandl.ModbusAssistant.sln          # .NET 8 解决方案（含主程序与单元测试项目）
├── Yuandl.ModbusAssistant/             # C# .NET 8 WPF 桌面应用
├── Yuandl.ModbusAssistant.Tests/       # xUnit 核心协议与从站单元测试
├── packaging/                          # Windows 发布脚本 (publish.ps1) 与 Inno Setup 安装包配置
├── .github/workflows/ci.yml            # 双端 GitHub Actions CI（Web Ubuntu + WPF Windows）
├── src/                                # React + TypeScript Web 前端
├── server.ts                           # Web 版 Express + WebSocket + 通讯后端
├── package.json                        # Web 版脚本与依赖
└── vite.config.ts                      # Vite 构建配置
```

## 运行与测试 WPF 桌面版

环境要求：**Windows 10/11**、**.NET 8 SDK**（含 Windows Desktop 支持）。在仓库根目录执行：

```powershell
dotnet restore .\Yuandl.ModbusAssistant.sln
dotnet build .\Yuandl.ModbusAssistant.sln --configuration Release
dotnet test .\Yuandl.ModbusAssistant.sln --configuration Release
dotnet run --project .\Yuandl.ModbusAssistant\Yuandl.ModbusAssistant.csproj
```

### 发布便携版与 Windows 安装包

使用仓库自带脚本一键执行测试、自包含发布（`dist\win-x64`）并可选生成 Inno Setup 安装向导：

```powershell
# 发布免安装目录版
.\packaging\publish.ps1

# 发布并生成 Inno Setup 安装包（需本机已安装 Inno Setup 6）
.\packaging\publish.ps1 -BuildInstaller
```

## 运行 Web 版

环境要求：**Node.js 20 LTS** 与 npm。

```bash
npm install
npm run dev
```

启动后在浏览器打开 [http://localhost:3000](http://localhost:3000)。

常用命令：

```bash
npm run lint   # ESLint 代码检查
npm run build  # 构建前端生产资源
npm start      # 以生产模式启动服务（需先执行 npm run build）
```

## 使用提示

- 多寄存器字节序中，`WordSwap` 保持每个 16 位寄存器内部字节顺序并反转 16 位寄存器顺序；`Double` 的 `ABCDEFGH` 因此变为 `GHEFCDAB`。WPF 与 Web 版使用完全一致的编解码规则。
- 在现场 PLC 或生产设备上执行写入（特别是广播写入）前，请确认站号、寄存器地址映射及写入权限已获授权。
