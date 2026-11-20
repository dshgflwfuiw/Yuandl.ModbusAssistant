# Web 版技术与接口文档

Web 版基于 **React 19 + TypeScript + Vite + Tailwind CSS + Express + WebSocket (`ws`)** 构建，适用于跨平台浏览器调试、Linux/容器联调以及无桌面安装环境的快速验证。

## 1. 架构与通讯边界

- **前端 (`src/`)**：单页应用，通过 HTTP REST 与 `/ws` WebSocket 与本地 Node 后端同步链路状态、实时帧流量（TX/RX/ERR）、示波器采样与从站寄存器变化。
- **后端 (`server.ts` + `src/server/modbusCore.ts`)**：
  - 使用 Node `net` 与 `dgram` 提供真实的 **TCP Client**（含单监督循环断线自动重连）、**UDP**、**无协议多客户端 TCP Server** 及 **Modbus TCP Server**。
  - **串口说明**：浏览器与通用容器中运行的 Web 版串口为内置 **虚拟串口仿真通道**（`SerialPort` 仿真与 Modbus RTU/ASCII 帧切分联调），不直接访问宿主机物理 COM 口；物理 RS232/RS485 硬件通信请使用 WPF 桌面端。

## 2. TCP Server 安全参数

与桌面端对齐，Web 端无协议 TCP Server 与内置 Modbus TCP Server 同样支持：

- `bindAddress`：监听绑定地址（默认 `127.0.0.1`，可按需配置 `0.0.0.0` 或指定网卡）
- `maxClients`：最大并发客户端会话数（默认 `32`）
- `idleTimeoutMs`：空闲断开超时毫秒数（默认 `60000`，`0` 为不限）
- `maxFrameLength`：单包最大字节限制（默认无协议 `4096` / Modbus TCP `260`）
- `sendQueueLimit`：发送缓冲队列阈值（默认 `64`）
- `allowedClientIps`：逗号分隔允许接入的客户端 IP 白名单（留空则不限制）

## 3. 主要 HTTP / WebSocket 接口

- `GET /api/state`：获取当前通讯快照、设备方案与系统设置
- `POST /api/comm/connect` / `POST /api/comm/disconnect`：连接或断开通道
- `POST /api/comm/send`：发送无协议原始字节（支持 `sessionId` 定向或广播）
- `POST /api/comm/server-clients/:sessionId/disconnect`：强制断开指定 TCP Server 客户端
- `POST /api/modbus/exchange`：执行 Modbus 请求并匹配响应（支持 `timeoutMs` / `retries` / `UnitId=0` 广播）
- `POST /api/server/start` / `POST /api/server/stop`：启停内置 Modbus 从站（`Tcp` / `SerialRtu` / `SerialAscii` / `TcpAndSerial`）
- `POST /api/server/registers` / `POST /api/server/registers/runtime` / `POST /api/server/registers/reset` / `DELETE /api/server/registers`：从站数据定义维护与在线改值
- `POST /api/server/import` / `POST /api/state/restore`：导入与恢复配置（自动兼容升级版本字段）
- `WS /ws`：推送 `state`、`dataReceived`、`traffic`、`error` 事件

## 4. 本地开发与校验命令

```bash
npm install
npm run lint        # ESLint 检查
npx tsc --noEmit    # TypeScript 类型检查
npm test            # Node 内置测试运行器执行 Modbus 协议与服务端压力集成测试
npm run build       # 构建 dist 前端产物
npm run dev         # 启动端口 3000 的全栈开发服务
```
