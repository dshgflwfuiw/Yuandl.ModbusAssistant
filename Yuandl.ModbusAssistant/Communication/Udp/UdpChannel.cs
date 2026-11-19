using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Communication.Udp;

/// <summary>
/// UDP 通讯通道
/// 实现基于无连接数据报的异步收发机制
/// </summary>
public class UdpChannel : ChannelBase
{
    private readonly UdpParameters _parameters;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;

    public override CommunicationType Type => CommunicationType.Udp;
    public override string ChannelInfo => _parameters.ToString();

    public UdpChannel(UdpParameters parameters)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public override Task<bool> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (State == ChannelState.Connected)
        {
            return Task.FromResult(true);
        }

        State = ChannelState.Connecting;

        try
        {
            _udpClient = _parameters.LocalPort > 0
                ? new UdpClient(_parameters.LocalPort)
                : new UdpClient();

            // 预先建立默认远程目标端点，简化后续 SendAsync
            _udpClient.Connect(_parameters.RemoteHost, _parameters.RemotePort);

            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReceiveLoopAsync(_readCts.Token), _readCts.Token);

            State = ChannelState.Connected;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            OnError(ex);
            _ = CloseAsync();
            throw;
        }
    }

    public override async Task CloseAsync()
    {
        if (State == ChannelState.Disconnected)
        {
            return;
        }

        try
        {
            _readCts?.Cancel();
            if (_readTask != null)
            {
                await Task.WhenAny(_readTask, Task.Delay(500)).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            _readCts?.Dispose();
            _readCts = null;
            _readTask = null;
        }

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
        finally
        {
            _udpClient = null;
            State = ChannelState.Disconnected;
        }
    }

    protected override async Task<int> SendInternalAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_udpClient == null)
        {
            throw new InvalidOperationException("UDP 客户端尚未初始化");
        }

        return await _udpClient.SendAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var client = _udpClient;

        while (!cancellationToken.IsCancellationRequested && client != null)
        {
            try
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length > 0)
                {
                    OnDataReceived(result.Buffer);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    OnError(ex);
                    State = ChannelState.Faulted;
                }
                break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _readCts?.Cancel();
            _udpClient?.Dispose();
        }
        base.Dispose(disposing);
    }
}
