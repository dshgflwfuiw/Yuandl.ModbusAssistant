using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Communication.Tcp;

/// <summary>
/// TCP 客户端通讯通道
/// 支持异步连接、超时控制、断线自动重连及远程主机被动关闭侦测
/// </summary>
public class TcpClientChannel : ChannelBase
{
    private readonly TcpClientParameters _parameters;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;

    public override CommunicationType Type => CommunicationType.TcpClient;
    public override string ChannelInfo => _parameters.ToString();

    public TcpClientChannel(TcpClientParameters parameters)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public override async Task<bool> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (State == ChannelState.Connected)
        {
            return true;
        }

        State = ChannelState.Connecting;

        try
        {
            _tcpClient = new TcpClient
            {
                NoDelay = true, // 禁用 Nagle 算法，降低工业报文延时
                ReceiveTimeout = _parameters.ReadTimeoutMs,
                SendTimeout = _parameters.WriteTimeoutMs
            };

            // 使用 ConnectAsync 并结合配置的连接超时时间
            using var timeoutCts = new CancellationTokenSource(_parameters.ConnectTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await _tcpClient.ConnectAsync(_parameters.Host, _parameters.Port, linkedCts.Token).ConfigureAwait(false);
            _networkStream = _tcpClient.GetStream();

            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);

            State = ChannelState.Connected;
            return true;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            OnError(ex);
            await CloseInternalAsync(false).ConfigureAwait(false);

            if (_parameters.AutoReconnect)
            {
                StartReconnectLoop();
            }

            throw;
        }
    }

    public override async Task CloseAsync()
    {
        StopReconnectLoop();
        await CloseInternalAsync(true).ConfigureAwait(false);
    }

    private async Task CloseInternalAsync(bool setDisconnectedState)
    {
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
            _networkStream?.Dispose();
            _tcpClient?.Close();
            _tcpClient?.Dispose();
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
        finally
        {
            _networkStream = null;
            _tcpClient = null;
            if (setDisconnectedState)
            {
                State = ChannelState.Disconnected;
            }
        }
    }

    protected override async Task<int> SendInternalAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_networkStream == null || !_networkStream.CanWrite)
        {
            throw new InvalidOperationException("网络流不可写或连接已关闭");
        }

        await _networkStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return data.Length;
    }

    /// <summary>
    /// 后台独立连续读取循环
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        var stream = _networkStream;

        while (!cancellationToken.IsCancellationRequested && stream != null && stream.CanRead)
        {
            try
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    // 接收到 EOF (0 字节)，代表对端服务器已主动关闭 TCP 连接
                    throw new IOException("远程主机已关闭连接 (EOF)");
                }

                OnDataReceived(new ReadOnlyMemory<byte>(buffer, 0, bytesRead));
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

                    if (_parameters.AutoReconnect)
                    {
                        StartReconnectLoop();
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// 触发自动重连后台任务
    /// </summary>
    private void StartReconnectLoop()
    {
        StopReconnectLoop();
        _reconnectCts = new CancellationTokenSource();
        _reconnectTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token), _reconnectCts.Token);
    }

    private void StopReconnectLoop()
    {
        try
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _reconnectCts = null;
            _reconnectTask = null;
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        State = ChannelState.Reconnecting;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_parameters.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) break;

                await CloseInternalAsync(false).ConfigureAwait(false);
                if (await TryReconnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 记录重连失败信息并继续下次尝试
                OnError(new Exception($"自动重连失败，将在 {_parameters.ReconnectIntervalMs}ms 后重试: {ex.Message}", ex));
            }
        }
    }

    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        State = ChannelState.Connecting;

        try
        {
            var tcpClient = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = _parameters.ReadTimeoutMs,
                SendTimeout = _parameters.WriteTimeoutMs
            };

            using var timeoutCts = new CancellationTokenSource(_parameters.ConnectTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await tcpClient.ConnectAsync(_parameters.Host, _parameters.Port, linkedCts.Token).ConfigureAwait(false);

            _tcpClient = tcpClient;
            _networkStream = tcpClient.GetStream();
            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);
            State = ChannelState.Connected;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnError(new Exception($"自动重连失败，将在 {_parameters.ReconnectIntervalMs}ms 后重试: {ex.Message}", ex));
            State = ChannelState.Faulted;
            await CloseInternalAsync(false).ConfigureAwait(false);
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopReconnectLoop();
            _readCts?.Cancel();
            _networkStream?.Dispose();
            _tcpClient?.Dispose();
        }
        base.Dispose(disposing);
    }
}
