using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;

namespace Yuandl.ModbusAssistant.Communication;

public sealed class ChannelDataReceivedEventArgs : EventArgs
{
    public required byte[] Bytes { get; init; }
    public string? SessionId { get; init; }
    public string? SessionLabel { get; init; }
}

public interface IChannel : IDisposable
{
    CommunicationType Type { get; }
    ChannelState State { get; }
    ChannelStatistics Statistics { get; }
    event EventHandler<ChannelState>? StateChanged;
    event EventHandler<ChannelDataReceivedEventArgs>? DataReceived;
    event EventHandler<string>? ErrorOccurred;

    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
    Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default);
}

public abstract class ChannelBase : IChannel
{
    public abstract CommunicationType Type { get; }

    private ChannelState _state = ChannelState.Disconnected;
    public ChannelState State
    {
        get => _state;
        protected set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public ChannelStatistics Statistics { get; } = new();

    public event EventHandler<ChannelState>? StateChanged;
    public event EventHandler<ChannelDataReceivedEventArgs>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;

    public abstract Task OpenAsync(CancellationToken cancellationToken = default);
    public abstract Task CloseAsync();
    public abstract Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default);

    protected void OnDataReceived(byte[] data, string? sessionId = null, string? sessionLabel = null)
    {
        Statistics.BytesReceived += data.Length;
        Statistics.PacketsReceived++;
        DataReceived?.Invoke(this, new ChannelDataReceivedEventArgs
        {
            Bytes = data,
            SessionId = sessionId,
            SessionLabel = sessionLabel
        });
    }

    protected void RecordSent(int byteCount)
    {
        Statistics.BytesSent += byteCount;
        Statistics.PacketsSent++;
    }

    protected void OnError(string message)
    {
        Statistics.ErrorsCount++;
        ErrorOccurred?.Invoke(this, message);
    }

    public virtual void Dispose()
    {
        _ = CloseAsync();
    }
}

public sealed class SerialPortChannel : ChannelBase
{
    private readonly SerialPortParameters _parameters;
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readLoopTask;

    public SerialPortChannel(SerialPortParameters parameters)
    {
        _parameters = parameters.Clone();
    }

    public override CommunicationType Type => CommunicationType.SerialPort;

    public override Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (State == ChannelState.Connected)
            return Task.CompletedTask;

        State = ChannelState.Connecting;
        try
        {
            var sp = new SerialPort(_parameters.PortName, _parameters.BaudRate, _parameters.Parity, _parameters.DataBits, _parameters.StopBits)
            {
                DtrEnable = _parameters.DtrEnable,
                RtsEnable = _parameters.RtsEnable,
                ReadTimeout = 500,
                WriteTimeout = 1000
            };
            sp.Open();
            _port = sp;
            _cts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
            State = ChannelState.Connected;
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            OnError($"串口打开失败: {ex.Message}");
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested && _port is { IsOpen: true })
        {
            try
            {
                int read = await _port.BaseStream.ReadAsync(buffer, token);
                if (read > 0)
                {
                    var packet = new byte[read];
                    Buffer.BlockCopy(buffer, 0, packet, 0, read);
                    OnDataReceived(packet);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    OnError($"串口读取异常: {ex.Message}");
                    State = ChannelState.Faulted;
                }
                break;
            }
        }
    }

    public override async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException("串口未连接");

        await _port.BaseStream.WriteAsync(data, cancellationToken);
        await _port.BaseStream.FlushAsync(cancellationToken);
        RecordSent(data.Length);
    }

    public override async Task CloseAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        if (_port != null)
        {
            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch { /* ignore close error */ }
            _port.Dispose();
            _port = null;
        }

        if (_readLoopTask != null)
        {
            try { await _readLoopTask; } catch { /* ignore */ }
            _readLoopTask = null;
        }

        State = ChannelState.Disconnected;
    }
}

public sealed class TcpClientChannel : ChannelBase
{
    private readonly TcpClientParameters _parameters;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoopTask;
    private bool _manualClosing;

    public TcpClientChannel(TcpClientParameters parameters)
    {
        _parameters = parameters;
    }

    public override CommunicationType Type => CommunicationType.TcpClient;

    public override async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        _manualClosing = false;
        State = ChannelState.Connecting;
        await ConnectCoreAsync(cancellationToken);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_parameters.ConnectTimeoutMs);

        try
        {
            await client.ConnectAsync(_parameters.Host, _parameters.Port, timeoutCts.Token);
            _client = client;
            _stream = client.GetStream();
            _cts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
            State = ChannelState.Connected;
        }
        catch (Exception ex)
        {
            client.Dispose();
            State = ChannelState.Faulted;
            OnError($"TCP 连接失败: {ex.Message}");
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested && _stream != null)
        {
            try
            {
                int read = await _stream.ReadAsync(buffer, token);
                if (read == 0)
                {
                    if (!_manualClosing)
                    {
                        OnError("TCP 远端连接已断开");
                        await HandleDisconnectAndMaybeReconnectAsync(token);
                    }
                    break;
                }

                var packet = new byte[read];
                Buffer.BlockCopy(buffer, 0, packet, 0, read);
                OnDataReceived(packet);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && !_manualClosing)
                {
                    OnError($"TCP 读取异常: {ex.Message}");
                    await HandleDisconnectAndMaybeReconnectAsync(token);
                }
                break;
            }
        }
    }

    private async Task HandleDisconnectAndMaybeReconnectAsync(CancellationToken token)
    {
        CleanupSocket();
        if (_parameters.AutoReconnect && !_manualClosing && !token.IsCancellationRequested)
        {
            State = ChannelState.Reconnecting;
            while (!token.IsCancellationRequested && !_manualClosing)
            {
                try
                {
                    await Task.Delay(_parameters.ReconnectIntervalMs, token);
                    await ConnectCoreAsync(token);
                    return;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    State = ChannelState.Reconnecting;
                }
            }
        }
        else
        {
            State = ChannelState.Disconnected;
        }
    }

    private void CleanupSocket()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public override async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        if (_stream == null || _client is not { Connected: true })
            throw new InvalidOperationException("TCP 客户端未连接");

        await _stream.WriteAsync(data, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
        RecordSent(data.Length);
    }

    public override async Task CloseAsync()
    {
        _manualClosing = true;
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        CleanupSocket();
        if (_readLoopTask != null)
        {
            try { await _readLoopTask; } catch { }
            _readLoopTask = null;
        }
        State = ChannelState.Disconnected;
    }
}

public sealed class UdpChannel : ChannelBase
{
    private readonly UdpParameters _parameters;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _readLoopTask;

    public UdpChannel(UdpParameters parameters)
    {
        _parameters = parameters;
    }

    public override CommunicationType Type => CommunicationType.Udp;

    public override Task OpenAsync(CancellationToken cancellationToken = default)
    {
        State = ChannelState.Connecting;
        try
        {
            _udp = _parameters.LocalPort > 0
                ? new UdpClient(_parameters.LocalPort)
                : new UdpClient(0);
            _cts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
            State = ChannelState.Connected;
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            OnError($"UDP 初始化失败: {ex.Message}");
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp != null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token);
                if (result.Buffer.Length > 0)
                    OnDataReceived(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    OnError($"UDP 接收异常: {ex.Message}");
                }
                break;
            }
        }
    }

    public override async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        if (_udp == null)
            throw new InvalidOperationException("UDP 通道未开启");

        await _udp.SendAsync(data, data.Length, _parameters.RemoteHost, _parameters.RemotePort);
        RecordSent(data.Length);
    }

    public override async Task CloseAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        _udp?.Dispose();
        _udp = null;
        if (_readLoopTask != null)
        {
            try { await _readLoopTask; } catch { }
            _readLoopTask = null;
        }
        State = ChannelState.Disconnected;
    }
}

public sealed class TcpServerChannel : ChannelBase
{
    private sealed class ClientEntry
    {
        public required TcpServerClientSession Info { get; init; }
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }
    }

    private readonly TcpServerParameters _parameters;
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private int _nextClientNumber;

    public TcpServerChannel(TcpServerParameters parameters)
    {
        _parameters = parameters;
    }

    public override CommunicationType Type => CommunicationType.TcpServer;

    public event EventHandler<IReadOnlyList<TcpServerClientSession>>? ClientsChanged;

    public IReadOnlyList<TcpServerClientSession> GetClients() =>
        _clients.Values.Select(c => new TcpServerClientSession
        {
            SessionId = c.Info.SessionId,
            RemoteAddress = c.Info.RemoteAddress,
            RemotePort = c.Info.RemotePort,
            ConnectedAt = c.Info.ConnectedAt,
            BytesSent = c.Info.BytesSent,
            BytesReceived = c.Info.BytesReceived
        }).OrderBy(c => c.ConnectedAt).ToList();

    public override Task OpenAsync(CancellationToken cancellationToken = default)
    {
        State = ChannelState.Connecting;
        try
        {
            var listener = new TcpListener(IPAddress.Any, _parameters.ListenPort);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
            State = ChannelState.Connected;
            NotifyClientsChanged();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            OnError($"TCP 服务端监听启动失败: {ex.Message}");
            throw;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                string sessionId = $"C{Interlocked.Increment(ref _nextClientNumber)}";
                var info = new TcpServerClientSession
                {
                    SessionId = sessionId,
                    RemoteAddress = endpoint?.Address.ToString() ?? "unknown",
                    RemotePort = endpoint?.Port ?? 0,
                    ConnectedAt = DateTime.Now
                };

                var entry = new ClientEntry
                {
                    Info = info,
                    Client = client,
                    Stream = client.GetStream()
                };

                _clients[sessionId] = entry;
                NotifyClientsChanged();
                _ = Task.Run(() => ClientReadLoopAsync(entry, token), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    OnError($"TCP 服务端接收连接异常: {ex.Message}");
                break;
            }
        }
    }

    private async Task ClientReadLoopAsync(ClientEntry entry, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested && entry.Client.Connected)
            {
                int read = await entry.Stream.ReadAsync(buffer, token);
                if (read == 0)
                    break;

                var packet = new byte[read];
                Buffer.BlockCopy(buffer, 0, packet, 0, read);
                entry.Info.BytesReceived += read;
                NotifyClientsChanged();
                OnDataReceived(packet, entry.Info.SessionId, entry.Info.DisplayLabel);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                OnError($"客户端 {entry.Info.SessionId} 读取异常: {ex.Message}");
        }
        finally
        {
            DisconnectClient(entry.Info.SessionId);
        }
    }

    public void DisconnectClient(string sessionId)
    {
        if (_clients.TryRemove(sessionId, out var entry))
        {
            try { entry.Stream.Dispose(); } catch { }
            try { entry.Client.Dispose(); } catch { }
            NotifyClientsChanged();
        }
    }

    public override async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        if (_clients.IsEmpty)
            throw new InvalidOperationException("当前无已连接的 TCP 客户端");

        IEnumerable<ClientEntry> targets;
        if (!string.IsNullOrWhiteSpace(targetSessionId))
        {
            if (!_clients.TryGetValue(targetSessionId, out var single))
                throw new InvalidOperationException($"客户端会话 {targetSessionId} 已断开");
            targets = new[] { single };
        }
        else
        {
            targets = _clients.Values.ToArray();
        }

        foreach (var target in targets)
        {
            try
            {
                await target.Stream.WriteAsync(data, cancellationToken);
                await target.Stream.FlushAsync(cancellationToken);
                target.Info.BytesSent += data.Length;
                RecordSent(data.Length);
            }
            catch (Exception ex)
            {
                OnError($"向 {target.Info.SessionId} 发送失败: {ex.Message}");
                DisconnectClient(target.Info.SessionId);
            }
        }
        NotifyClientsChanged();
    }

    private void NotifyClientsChanged()
    {
        ClientsChanged?.Invoke(this, GetClients());
    }

    public override async Task CloseAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        try { _listener?.Stop(); } catch { }
        _listener = null;

        foreach (var id in _clients.Keys.ToArray())
            DisconnectClient(id);

        if (_acceptLoopTask != null)
        {
            try { await _acceptLoopTask; } catch { }
            _acceptLoopTask = null;
        }

        State = ChannelState.Disconnected;
    }
}
