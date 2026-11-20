# Modbus 协议、四字序与点表规范

本项目双端（C# WPF 与 TypeScript/Node Web）严格遵守同一套 Modbus 协议解析规则、字序变换算法与黄金测试向量（`test-vectors/modbus-vectors.json`）。

## 1. 支持的传输格式与功能码

- **传输模式**：
  - **Modbus TCP**：7 字节 MBAP 头（`TransactionId[2] + ProtocolId(0)[2] + Length[2] + UnitId[1]`）+ PDU。
  - **Modbus RTU**：`UnitId[1] + PDU + CRC16(Low, High)[2]`，多项式 `0xA001`。
  - **Modbus ASCII**：`:` + Hex(`UnitId + PDU + LRC`) + `\r\n`。
- **支持功能码**：
  - 读操作：`0x01`（读线圈）、`0x02`（读离散输入）、`0x03`（读保持寄存器）、`0x04`（读输入寄存器）
  - 单点写：`0x05`（写单个线圈，`0xFF00` / `0x0000`）、`0x06`（写单个寄存器）
  - 批量写：`0x0F`（写多个线圈）、`0x10`（写多个寄存器）

## 2. 广播语义 (`UnitId = 0`)

- Modbus 规范规定站号 `0` 为广播写入通道，仅对写入类功能码（`05 / 06 / 0F / 10`）有效。
- 主站发送广播写请求后静默等待总线窗口（默认 35 ms）并直接返回空响应，**不会触发无响应超时重传**。
- 从站在收到有效站号 `0` 写入请求时更新内部寄存器/线圈状态，并严格抑制回包；对站号 `0` 的读取请求直接忽略不响应。

## 3. 多寄存器字序与字节序规则

| 枚举名 | 别名 | 16 位示例 (`AB`) | 32 位示例 (`ABCD`) | 64 位示例 (`ABCDEFGH`) |
| :--- | :--- | :--- | :--- | :--- |
| `BigEndian` | 大端序 | `AB` | `ABCD` | `ABCDEFGH` |
| `LittleEndian` | 全字节反转 | `BA` | `DCBA` | `HGFEDCBA` |
| `WordSwap` | 16 位字序交换 | `AB` | `CDAB` | `GHEFCDAB` |
| `ByteSwap` | 字内高低字节交换 | `BA` | `BADC` | `BADCFEHG` |

> 双端均通过 `test-vectors/modbus-vectors.json` 中的基准用例验证 `Float` 与 `Double` 在上述 4 种字节序下的往返编解码一致性。

## 4. 服务器点表 CSV 格式与导入校验

标准导出/导入表头为：

```csv
Area,Address,DataType,ByteOrder,InitialValue,CurrentValue,Note
HoldingRegister,0,UInt16,BigEndian,100,100,"温度设定"
HoldingRegister,2,Float,WordSwap,36.5,36.5,"流量计, 通道A"
Coil,0,Bit,BigEndian,true,true,"运行使能"
```

导入校验强约束：
1. **文件体积限制**：单次导入最大允许 `5 MB`（`5,242,880` 字节）。
2. **编码与转义**：自动识别 UTF-8 BOM；完整支持 RFC-4180 多行与双引号转义。
3. **区段与数据类型约束**：`Coil`/`DiscreteInput` 仅允许 `Bit`；`HoldingRegister`/`InputRegister` 仅允许数值型（`Int16`/`UInt16`/`Int32`/`UInt32`/`Float`/`Double`）。
4. **宽度与越界检查**：`Address + Width <= 65536`（`Float`/`Int32`/`UInt32` 占 2 个寄存器，`Double` 占 4 个寄存器）。
5. **地址重叠检查**：同一存储区内的任意多字节/单字定义不允许存在地址区间重叠。
6. **初始值数值范围检查**：按目标类型与边界严格解析（非 `NaN` / `Infinity`）。
