using Yuandl.ModbusAssistant.Core.Enums;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

/// <summary>
/// 协议适配器抽象边界：
/// 本项目以自研确定性 Modbus RTU/ASCII/TCP 协议栈及 <c>ModbusServerService</c> 作为唯一事实来源，
/// 以确保桌面端与 Web 端在异常码、四字序和广播语义上严格一致；
/// 若后续接入第三方驱动（例如 HslCommunication 用于扩展私有 PLC 协议），应通过独立适配器隔离，
/// 禁止同时维护两套并行 Modbus 服务端实例。
/// </summary>
public interface IModbusClientAdapter
{
    string ProviderName { get; }

    Task<byte[]> ExchangeRawAsync(
        byte[] request,
        ModbusTransport transport,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken = default);
}
