using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Services;

public sealed class CommunicationService : IDisposable
{
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private IChannel? _activeChannel;

    public ChannelState State => _activeChannel?.State ?? ChannelState.Disconnected;
    public CommunicationType? ActiveType => _activeChannel?.Type;
    public ChannelStatistics Statistics => _activeChannel?.Statistics ?? new ChannelStatistics();

    public IReadOnlyList<TcpServerClientSession> ConnectedClients =>
        _activeChannel is TcpServerChannel tcpServer ? tcpServer.GetClients() : Array.Empty<TcpServerClientSession>();

    public event EventHandler<ChannelState>? StateChanged;
    public event EventHandler<ChannelDataReceivedEventArgs>? DataReceived;
    public event EventHandler<TrafficEventArgs>? TrafficCaptured;
    public event EventHandler<IReadOnlyList<TcpServerClientSession>>? ClientsChanged;
    public event EventHandler<string>? ErrorOccurred;

    public async Task ConnectSerialAsync(SerialPortParameters parameters, ModbusServerService? modbusServer = null)
    {
        await DisconnectAsync();
        if (modbusServer != null && modbusServer.IsSerialEndpointMatch(parameters))
        {
            await OpenChannelAsync(new VirtualLoopbackSerialChannel(parameters, modbusServer.HandleSerialRequest));
            return;
        }

        try
        {
            await OpenChannelAsync(new SerialPortChannel(parameters));
        }
        catch
        {
            if (modbusServer != null && modbusServer.IsSerialEndpointMatch(parameters))
                await OpenChannelAsync(new VirtualLoopbackSerialChannel(parameters, modbusServer.HandleSerialRequest));
            else
                throw;
        }
    }

    public async Task ConnectTcpClientAsync(TcpClientParameters parameters)
    {
        await DisconnectAsync();
        await OpenChannelAsync(new TcpClientChannel(parameters));
    }

    public async Task ConnectUdpAsync(UdpParameters parameters)
    {
        await DisconnectAsync();
        await OpenChannelAsync(new UdpChannel(parameters));
    }

    public async Task StartTcpServerAsync(TcpServerParameters parameters)
    {
        await DisconnectAsync();
        await OpenChannelAsync(new TcpServerChannel(parameters));
    }

    private async Task OpenChannelAsync(IChannel channel)
    {
        channel.StateChanged += OnChannelStateChanged;
        channel.DataReceived += OnChannelDataReceived;
        channel.ErrorOccurred += OnChannelErrorOccurred;
        if (channel is TcpServerChannel srv)
            srv.ClientsChanged += OnServerClientsChanged;

        _activeChannel = channel;
        try
        {
            await channel.OpenAsync();
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var ch = _activeChannel;
        _activeChannel = null;
        if (ch != null)
        {
            ch.StateChanged -= OnChannelStateChanged;
            ch.DataReceived -= OnChannelDataReceived;
            ch.ErrorOccurred -= OnChannelErrorOccurred;
            if (ch is TcpServerChannel srv)
                srv.ClientsChanged -= OnServerClientsChanged;

            await ch.CloseAsync();
            ch.Dispose();
        }
        StateChanged?.Invoke(this, ChannelState.Disconnected);
        ClientsChanged?.Invoke(this, Array.Empty<TcpServerClientSession>());
    }

    public async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        if (_activeChannel == null || _activeChannel.State != ChannelState.Connected)
            throw new InvalidOperationException("当前无活跃连接通道");

        await _activeChannel.SendAsync(data, targetSessionId, cancellationToken);
        TrafficCaptured?.Invoke(this, new TrafficEventArgs
        {
            Direction = "TX",
            Bytes = data,
            Hex = ByteUtils.BytesToHex(data),
            Timestamp = DateTime.Now,
            ChannelType = _activeChannel.Type,
            SessionId = targetSessionId,
            SessionLabel = targetSessionId != null ? $"-> {targetSessionId}" : null
        });
    }

    public void DisconnectServerClient(string sessionId)
    {
        if (_activeChannel is TcpServerChannel srv)
            srv.DisconnectClient(sessionId);
    }

    public async Task<byte[]> ExchangeModbusAsync(
        byte[] request,
        ModbusTransport transport,
        int timeoutMs = 1000,
        int retries = 2,
        CancellationToken cancellationToken = default)
    {
        if (_activeChannel == null || _activeChannel.State != ChannelState.Connected)
            throw new InvalidOperationException("请先在通讯连接页建立连接");

        await _exchangeLock.WaitAsync(cancellationToken);
        try
        {
            Exception? lastException = null;
            int maxAttempts = Math.Max(1, retries + 1);

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                var accumulator = new List<byte>();

                void Handler(object? _, ChannelDataReceivedEventArgs e)
                {
                    lock (accumulator)
                    {
                        accumulator.AddRange(e.Bytes);
                        var snapshot = accumulator.ToArray();
                        if (IsCompleteModbusFrame(snapshot, transport))
                        {
                            tcs.TrySetResult(snapshot);
                        }
                    }
                }

                _activeChannel.DataReceived += Handler;
                try
                {
                    await SendAsync(request, null, cancellationToken);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(Math.Clamp(timeoutMs, 100, 30000));
                    using var reg = timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException("Modbus 响应超时")));

                    return await tcs.Task;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    lastException = ex;
                }
                finally
                {
                    _activeChannel.DataReceived -= Handler;
                }
            }

            throw lastException ?? new TimeoutException("Modbus 请求未收到响应");
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    private static bool IsCompleteModbusFrame(byte[] buffer, ModbusTransport transport)
    {
        if (buffer.Length == 0) return false;

        if (transport == ModbusTransport.Tcp)
        {
            if (buffer.Length < 8) return false;
            int declaredLength = (buffer[4] << 8) | buffer[5];
            return buffer.Length >= 6 + declaredLength;
        }

        if (transport == ModbusTransport.Ascii)
        {
            return buffer.Length >= 7 && buffer[0] == (byte)':' && buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n';
        }

        try
        {
            _ = ModbusFrame.ParseResponse(buffer, ModbusTransport.Rtu);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnChannelStateChanged(object? sender, ChannelState state) => StateChanged?.Invoke(this, state);

    private void OnChannelDataReceived(object? sender, ChannelDataReceivedEventArgs e)
    {
        DataReceived?.Invoke(this, e);
        TrafficCaptured?.Invoke(this, new TrafficEventArgs
        {
            Direction = "RX",
            Bytes = e.Bytes,
            Hex = ByteUtils.BytesToHex(e.Bytes),
            Timestamp = DateTime.Now,
            ChannelType = _activeChannel?.Type,
            SessionId = e.SessionId,
            SessionLabel = e.SessionLabel
        });
    }

    private void OnServerClientsChanged(object? sender, IReadOnlyList<TcpServerClientSession> clients) =>
        ClientsChanged?.Invoke(this, clients);

    private void OnChannelErrorOccurred(object? sender, string message) => ErrorOccurred?.Invoke(this, message);

    public void Dispose()
    {
        _ = DisconnectAsync();
        _exchangeLock.Dispose();
    }
}
