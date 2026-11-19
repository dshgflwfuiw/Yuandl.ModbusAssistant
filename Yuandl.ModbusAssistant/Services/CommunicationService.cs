using System;
using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Services;

/// <summary>
/// 全局通讯管理服务实现
/// 负责通道创建、状态转发、异常通知与生命周期控制
/// </summary>
public class CommunicationService : ICommunicationService
{
    private readonly IChannelFactory _channelFactory;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private IChannel? _activeChannel;
    private readonly ChannelStatistics _aggregateStatistics = new();

    public IChannel? ActiveChannel => _activeChannel;
    public bool IsConnected => _activeChannel?.State == ChannelState.Connected;
    public ChannelState CurrentState => _activeChannel?.State ?? ChannelState.Disconnected;
    public CommunicationType? CurrentType => _activeChannel?.Type;
    public string CurrentChannelInfo => _activeChannel?.ChannelInfo ?? "无活动连接";
    public ChannelStatistics Statistics => _activeChannel?.Statistics ?? _aggregateStatistics;

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public CommunicationService(IChannelFactory channelFactory)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
    }

    public async Task<bool> ConnectSerialAsync(SerialPortParameters parameters, CancellationToken cancellationToken = default)
    {
        var channel = _channelFactory.CreateSerialChannel(parameters);
        return await SwitchChannelAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConnectTcpAsync(TcpClientParameters parameters, CancellationToken cancellationToken = default)
    {
        var channel = _channelFactory.CreateTcpClientChannel(parameters);
        return await SwitchChannelAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConnectUdpAsync(UdpParameters parameters, CancellationToken cancellationToken = default)
    {
        var channel = _channelFactory.CreateUdpChannel(parameters);
        return await SwitchChannelAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _channelLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeChannel != null)
            {
                UnsubscribeEvents(_activeChannel);
                await _activeChannel.CloseAsync().ConfigureAwait(false);
                await _activeChannel.DisposeAsync().ConfigureAwait(false);
                _activeChannel = null;
                StateChanged?.Invoke(this, new ChannelStateChangedEventArgs(ChannelState.Connected, ChannelState.Disconnected, "已断开连接"));
            }
        }
        finally
        {
            _channelLock.Release();
        }
    }

    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var channel = _activeChannel;
        if (channel == null || channel.State != ChannelState.Connected)
        {
            throw new InvalidOperationException("通讯链路未就绪或已断开，无法发送数据");
        }

        return await channel.SendAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public void ResetStatistics()
    {
        _activeChannel?.Statistics.Reset();
        _aggregateStatistics.Reset();
    }

    private async Task<bool> SwitchChannelAsync(IChannel newChannel, CancellationToken cancellationToken)
    {
        await _channelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeChannel != null)
            {
                UnsubscribeEvents(_activeChannel);
                await _activeChannel.CloseAsync().ConfigureAwait(false);
                await _activeChannel.DisposeAsync().ConfigureAwait(false);
                _activeChannel = null;
            }

            SubscribeEvents(newChannel);
            _activeChannel = newChannel;

            bool opened = await newChannel.OpenAsync(cancellationToken).ConfigureAwait(false);
            return opened;
        }
        catch (Exception ex)
        {
            if (_activeChannel != null)
            {
                UnsubscribeEvents(_activeChannel);
                await _activeChannel.DisposeAsync().ConfigureAwait(false);
                _activeChannel = null;
            }
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private void SubscribeEvents(IChannel channel)
    {
        channel.DataReceived += OnChannelDataReceived;
        channel.StateChanged += OnChannelStateChanged;
        channel.ErrorOccurred += OnChannelErrorOccurred;
    }

    private void UnsubscribeEvents(IChannel channel)
    {
        channel.DataReceived -= OnChannelDataReceived;
        channel.StateChanged -= OnChannelStateChanged;
        channel.ErrorOccurred -= OnChannelErrorOccurred;
    }

    private void OnChannelDataReceived(object? sender, DataReceivedEventArgs e)
    {
        DataReceived?.Invoke(this, e);
    }

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e)
    {
        StateChanged?.Invoke(this, e);
    }

    private void OnChannelErrorOccurred(object? sender, Exception e)
    {
        ErrorOccurred?.Invoke(this, e);
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
        _channelLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _channelLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
