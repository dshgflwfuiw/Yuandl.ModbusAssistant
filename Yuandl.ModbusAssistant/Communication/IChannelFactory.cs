using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Communication;

/// <summary>
/// 通讯通道工厂契约
/// </summary>
public interface IChannelFactory
{
    IChannel CreateSerialChannel(SerialPortParameters parameters);
    IChannel CreateTcpClientChannel(TcpClientParameters parameters);
    IChannel CreateUdpChannel(UdpParameters parameters);
}
