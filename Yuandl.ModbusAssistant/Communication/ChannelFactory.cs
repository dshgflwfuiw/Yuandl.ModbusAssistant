using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Communication.Serial;
using Yuandl.ModbusAssistant.Communication.Tcp;
using Yuandl.ModbusAssistant.Communication.Udp;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Communication;

/// <summary>
/// 通道工厂实现
/// </summary>
public class ChannelFactory : IChannelFactory
{
    public IChannel CreateSerialChannel(SerialPortParameters parameters) => new SerialChannel(parameters);
    public IChannel CreateTcpClientChannel(TcpClientParameters parameters) => new TcpClientChannel(parameters);
    public IChannel CreateUdpChannel(UdpParameters parameters) => new UdpChannel(parameters);
}
