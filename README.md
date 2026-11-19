# Yuandl Modbus Assistant

Yuandl Modbus Assistant 是一个面向工业通信调试与设备联调的工具项目，包含 **React + TypeScript Web 版**和 **C# WPF 桌面版**。可用于 Modbus 报文调试、原始数据收发、通信状态观察及 Modbus 从站仿真。

## 功能概览

- **通信连接**：串口、TCP 客户端、UDP；支持 TCP 服务端及客户端会话管理。
- **Modbus 调试**：发送 Modbus 请求并查看响应，辅助分析异常响应。
- **无协议调试**：以原始字节、HEX 或 ASCII 形式收发数据。
- **数据监视与报文分析**：查看通信流量、收发统计、请求响应和时延等信息。
- **设备与配置管理**：管理设备配置，支持服务器寄存器配置导入。
- **Modbus 从站仿真**：配置线圈、离散输入、保持寄存器和输入寄存器；Web 版内置 Modbus TCP 服务。

> **串口实现说明：**WPF 桌面版使用系统串口；当前 Web 版的串口连接为虚拟/仿真通道，不会直接访问本机物理串口。

## 项目结构

```text
.
├── Yuandl.ModbusAssistant.sln       # .NET/WPF 解决方案
├── Yuandl.ModbusAssistant/          # C# WPF 桌面应用
├── src/                              # React + TypeScript Web 前端
├── server.ts                         # Web 版 Express 服务与通信服务
├── package.json                     # Web 版脚本与依赖
└── vite.config.ts                   # Vite 配置
```

## 运行 Web 版

需要安装 **Node.js**（建议使用当前 LTS 版本）和 npm。

```bash
npm install
npm run dev
```

启动后在浏览器打开 [http://localhost:3000](http://localhost:3000)。

常用命令：

```bash
npm run lint   # 代码检查
npm run build  # 构建前端资源
npm start      # 以生产模式启动服务（需先执行 npm run build）
```

默认服务端口为 `3000`。如需更改端口，请先检查并修改 `server.ts` 中的 `PORT` 常量。

## 构建 WPF 桌面版

需要 Windows、.NET 8 SDK（含 Windows Desktop 支持）。在仓库根目录执行：

```powershell
dotnet restore .\Yuandl.ModbusAssistant.sln
dotnet build .\Yuandl.ModbusAssistant.sln --configuration Release
dotnet run --project .\Yuandl.ModbusAssistant\Yuandl.ModbusAssistant.csproj
```

WPF 项目目标框架为 `net8.0-windows`，串口功能依赖 `System.IO.Ports`。

## 使用提示

- 调试前请确认连接类型、主机/端口或串口参数与目标设备一致。
- 使用内置 Modbus TCP 服务时，选择合适的监听端口和站号，并在客户端连接相应端点。
- 在网络或现场设备上测试前，请确认端口和设备配置已经获得授权，并避免向生产设备发送未经验证的写入请求。

## 技术栈

- Web：React、TypeScript、Vite、Tailwind CSS、Express、WebSocket
- 桌面端：C#、.NET 8、WPF
