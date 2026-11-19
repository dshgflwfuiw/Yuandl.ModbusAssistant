using System;
using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Services;

/// <summary>
/// 全局通讯管理服务契约
/// 供所有上层业务模块（Modbus 调试、Raw 调试、寄存器监视、报文分析）共享统一通道
/// </summary>
public interface ICommunicationService : IAsyncDisposable, IDisposable
{
    IChannel? ActiveChannel { get; }
    bool IsConnected { get; }
    ChannelState CurrentState { get; }
    CommunicationType? CurrentType { get; }
    string CurrentChannelInfo { get; }
    ChannelStatistics Statistics { get; }

    event EventHandler<DataReceivedEventArgs>? DataReceived;
    event EventHandler<ChannelStateChangedEventArgs>? StateChanged;
    event EventHandler<Exception>? ErrorOccurred;

    Task<bool> ConnectSerialAsync(SerialPortParameters parameters, CancellationToken cancellationToken = default);
    Task<bool> ConnectTcpAsync(TcpClientParameters parameters, CancellationToken cancellationToken = default);
    Task<bool> ConnectUdpAsync(UdpParameters parameters, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    void ResetStatistics();
}
