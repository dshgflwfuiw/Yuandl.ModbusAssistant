using System;
using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Communication.Abstractions;

/// <summary>
/// 通用底层通讯通道契约接口
/// 抽象所有物理通讯介质（Serial / TCP / UDP），对外提供统一的异步收发、状态监视与生命周期管理
/// </summary>
public interface IChannel : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// 当前通讯类型
    /// </summary>
    CommunicationType Type { get; }

    /// <summary>
    /// 当前通道状态
    /// </summary>
    ChannelState State { get; }

    /// <summary>
    /// 通道详细描述（如端口、IP:Port）
    /// </summary>
    string ChannelInfo { get; }

    /// <summary>
    /// 通道流量与健康度统计
    /// </summary>
    ChannelStatistics Statistics { get; }

    /// <summary>
    /// 数据接收事件
    /// </summary>
    event EventHandler<DataReceivedEventArgs>? DataReceived;

    /// <summary>
    /// 状态变更事件
    /// </summary>
    event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 异常发生事件
    /// </summary>
    event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// 异步打开连接通道
    /// </summary>
    Task<bool> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步关闭连接通道
    /// </summary>
    Task CloseAsync();

    /// <summary>
    /// 异步发送原始字节数据
    /// </summary>
    Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
