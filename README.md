# Yuandl Modbus Assistant

Yuandl Modbus Assistant 是一个面向工业通信调试、从站仿真与信号分析的双端开源工作站，包含 **C# .NET 8 WPF 桌面客户端** 与 **React + TypeScript + Express Web 版**，双方共享统一的 Modbus RTU/ASCII/TCP 编解码规范与跨端测试向量（`test-vectors/modbus-vectors.json`）。

## 双端定位

| 端 | 技术栈 | 核心能力 | 详细文档 |
| :--- | :--- | :--- | :--- |
| **WPF 桌面端** | .NET 8 + WPF + `Yuandl.ThemeUI 2.0.7` | 物理串口 / 虚拟环回、TCP/UDP 客户端与多客户端服务端、内置多模式 Modbus 从站、示波器实时采集、RTT 报文分析与本地配置文件持久化 | [WPF 桌面端文档](docs/wpf.md) |
| **Web 端** | React 19 + TypeScript + Tailwind + Express + `ws` | 浏览器端 Modbus 调试工作台、Node 后端 TCP/UDP/多客户端通道、内置 TCP/串口仿真 Modbus 从站、在线改值与实时波形监视 | [Web 端文档](docs/web.md) |

> **协议与驱动边界说明：**  
> 本项目采用自研的确定性 Modbus 协议栈（RTU / ASCII / TCP）与 `ModbusServerService`，不默认引入第三方商业协议组件（如 `HslCommunication.dll`）维护第二套从站实现，从而保证桌面端与 Web 端的异常码、字序规则、从站内存行为 100% 一致；WPF 端同时提供 `IModbusClientAdapter` 抽象接口作为可选扩展边界。详见 [协议规范与点表规则](docs/protocol.md)。

## 仓库目录结构

```text
.
├── Yuandl.ModbusAssistant.sln          # .NET 8 解决方案（WPF 主程序 + xUnit 测试）
├── Yuandl.ModbusAssistant/             # C# .NET 8 WPF 桌面工作站（接入 Yuandl.ThemeUI 2.0.7）
├── Yuandl.ModbusAssistant.Tests/       # xUnit 协议向量、TCP 安全边界、并发与重连测试
├── docs/                               # 分端与协议文档（wpf.md / web.md / protocol.md）
├── packaging/                          # Windows 自包含发布脚本与 Inno Setup 配置
├── .github/workflows/ci.yml            # 双端 GitHub Actions CI（Ubuntu Web + Windows WPF）
├── src/                                # React + TypeScript Web 前端与协议核心
├── server.ts                           # Web 版 Express HTTP API + WebSocket 服务入口
├── tests/                              # Node.js 跨端协议向量与通讯压力集成测试
├── test-vectors/                       # 双端共用的 Modbus 帧与四字序黄金测试向量
├── package.json                        # Web 端依赖与脚本
└── vite.config.ts                      # Vite 构建配置
```

## 快速开始

### WPF 桌面端（Windows 10/11 + .NET 8 SDK）

```powershell
dotnet restore .\Yuandl.ModbusAssistant.sln
dotnet build .\Yuandl.ModbusAssistant.sln --configuration Release
dotnet test .\Yuandl.ModbusAssistant.sln --configuration Release
dotnet run --project .\Yuandl.ModbusAssistant\Yuandl.ModbusAssistant.csproj
```

### Web 端（Node.js 20 LTS）

```bash
npm install
npm test
npm run dev
```

启动后在浏览器访问 `http://localhost:3000`。

## 文档索引

- [WPF 桌面端架构与运行说明](docs/wpf.md)
- [Web 端前后端与接口说明](docs/web.md)
- [Modbus 协议栈、四字序与点表规范](docs/protocol.md)
