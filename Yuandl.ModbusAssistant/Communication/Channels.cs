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

    private int _disposed;

    public virtual void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _ = CloseAsync();
        GC.SuppressFinalize(this);
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

        if (string.IsNullOrWhiteSpace(_parameters.PortName))
            throw new ArgumentException("串口名称不能为空", nameof(_parameters.PortName));
        if (_parameters.BaudRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(_parameters.BaudRate), "波特率必须大于 0");
        if (_parameters.DataBits is < 5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(_parameters.DataBits), "数据位必须在 5 至 8 之间");

        State = ChannelState.Connecting;
        try
        {
            var sp = new SerialPort(_parameters.PortName.Trim(), _parameters.BaudRate, _parameters.Parity, _parameters.DataBits, _parameters.StopBits)
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
        var port = _port;
        if (port is not { IsOpen: true })
            throw new InvalidOperationException("串口未连接");

        await port.BaseStream.WriteAsync(data, cancellationToken);
        await port.BaseStream.FlushAsync(cancellationToken);
        RecordSent(data.Length);
    }

    public override async Task CloseAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            try { await cts.CancelAsync(); } catch { /* ignore */ }
        }

        var port = Interlocked.Exchange(ref _port, null);
        if (port != null)
        {
            try
            {
                if (port.IsOpen) port.Close();
            }
            catch { /* ignore close error */ }
            port.Dispose();
        }

        var readTask = Interlocked.Exchange(ref _readLoopTask, null);
        if (readTask != null)
        {
            try { await readTask; } catch { /* ignore */ }
        }

        cts?.Dispose();
        State = ChannelState.Disconnected;
    }
}

public sealed class VirtualLoopbackSerialChannel : ChannelBase
{
    private readonly SerialPortParameters _parameters;
    private readonly Func<SerialPortParameters, byte[], byte[]?> _serverHandler;

    public VirtualLoopbackSerialChannel(
        SerialPortParameters parameters,
        Func<SerialPortParameters, byte[], byte[]?> serverHandler)
    {
        _parameters = parameters.Clone();
        _serverHandler = serverHandler;
    }

    public override CommunicationType Type => CommunicationType.SerialPort;

    public override Task OpenAsync(CancellationToken cancellationToken = default)
    {
        State = ChannelState.Connected;
        return Task.CompletedTask;
    }

    public override Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        if (State != ChannelState.Connected)
            throw new InvalidOperationException("虚拟串口未连接");

        RecordSent(data.Length);
        var reply = _serverHandler(_parameters, data);
        if (reply is { Length: > 0 })
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5, CancellationToken.None);
                if (State == ChannelState.Connected)
                    OnDataReceived(reply);
            }, CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public override Task CloseAsync()
    {
        State = ChannelState.Disconnected;
        return Task.CompletedTask;
    }
}

public sealed class TcpClientChannel : ChannelBase
{
    private readonly TcpClientParameters _parameters;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _supervisorTask;
    private volatile bool _manualClosing;

    public TcpClientChannel(TcpClientParameters parameters)
    {
        _parameters = parameters;
    }

    public override CommunicationType Type => CommunicationType.TcpClient;

    public override async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_parameters.Host))
            throw new ArgumentException("TCP 主机地址不能为空", nameof(_parameters.Host));
        if (_parameters.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(_parameters.Port), "TCP 端口必须在 1 至 65535 之间");

        await CloseAsync();

        _manualClosing = false;
        State = ChannelState.Connecting;
        var sessionCts = new CancellationTokenSource();
        _cts = sessionCts;

        try
        {
            await ConnectSocketAsync(cancellationToken, sessionCts.Token);
            _supervisorTask = Task.Run(() => SupervisorLoopAsync(sessionCts.Token), CancellationToken.None);
        }
        catch
        {
            CleanupSocket();
            var cts = Interlocked.Exchange(ref _cts, null);
            cts?.Dispose();
            State = ChannelState.Faulted;
            throw;
        }
    }

    private async Task ConnectSocketAsync(CancellationToken callerToken, CancellationToken sessionToken)
    {
        int connectTimeoutMs = Math.Clamp(_parameters.ConnectTimeoutMs, 100, 60000);
        var client = new TcpClient { NoDelay = true };
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, sessionToken);
        linkedCts.CancelAfter(connectTimeoutMs);

        try
        {
            await client.ConnectAsync(_parameters.Host.Trim(), _parameters.Port, linkedCts.Token);
            _client = client;
            _stream = client.GetStream();
            State = ChannelState.Connected;
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }
            OnError($"TCP 连接失败: {ex.Message}");
            throw;
        }
    }

    private async Task SupervisorLoopAsync(CancellationToken sessionToken)
    {
        int reconnectIntervalMs = Math.Clamp(_parameters.ReconnectIntervalMs, 200, 60000);

        while (!sessionToken.IsCancellationRequested && !_manualClosing)
        {
            await ReadCurrentStreamAsync(sessionToken);

            if (sessionToken.IsCancellationRequested || _manualClosing)
                break;

            CleanupSocket();

            if (!_parameters.AutoReconnect)
            {
                State = ChannelState.Disconnected;
                break;
            }

            State = ChannelState.Reconnecting;
            while (!sessionToken.IsCancellationRequested && !_manualClosing)
            {
                try
                {
                    await Task.Delay(reconnectIntervalMs, sessionToken);
                    if (sessionToken.IsCancellationRequested || _manualClosing)
                        return;

                    await ConnectSocketAsync(CancellationToken.None, sessionToken);
                    break; // connected -> return to outer ReadCurrentStreamAsync loop
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    State = ChannelState.Reconnecting;
                }
            }
        }
    }

    private async Task ReadCurrentStreamAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        var stream = _stream;
        while (!token.IsCancellationRequested && !_manualClosing && stream != null)
        {
            try
            {
                int read = await stream.ReadAsync(buffer, token);
                if (read == 0)
                {
                    if (!token.IsCancellationRequested && !_manualClosing)
                        OnError("TCP 远端连接已断开");
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
                    OnError($"TCP 读取异常: {ex.Message}");
                break;
            }
        }
    }

    private void CleanupSocket()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var client = Interlocked.Exchange(ref _client, null);
        try { stream?.Dispose(); } catch { }
        try { client?.Dispose(); } catch { }
    }

    public override async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        var client = _client;
        if (State != ChannelState.Connected || stream == null || client is not { Connected: true })
            throw new InvalidOperationException("TCP 客户端未连接");

        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        RecordSent(data.Length);
    }

    public override async Task CloseAsync()
    {
        _manualClosing = true;
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            try { await cts.CancelAsync(); } catch { }
        }

        CleanupSocket();

        var supervisor = Interlocked.Exchange(ref _supervisorTask, null);
        if (supervisor != null)
        {
            try { await supervisor; } catch { }
        }

        cts?.Dispose();
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
        if (string.IsNullOrWhiteSpace(_parameters.RemoteHost))
            throw new ArgumentException("UDP 远程主机不能为空", nameof(_parameters.RemoteHost));
        if (_parameters.RemotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(_parameters.RemotePort), "UDP 远程端口必须在 1 至 65535 之间");
        if (_parameters.LocalPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(_parameters.LocalPort), "UDP 本地端口必须在 0 至 65535 之间");

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
        while (!token.IsCancellationRequested)
        {
            var udp = _udp;
            if (udp == null) break;
            try
            {
                var result = await udp.ReceiveAsync(token);
                if (result.Buffer.Length > 0)
                    OnDataReceived(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    OnError($"UDP 接收异常: {ex.Message}");
                break;
            }
        }
    }

    public override async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        var udp = _udp;
        if (udp == null || State != ChannelState.Connected)
            throw new InvalidOperationException("UDP 通道未开启");

        await udp.SendAsync(data.AsMemory(), _parameters.RemoteHost.Trim(), _parameters.RemotePort, cancellationToken);
        RecordSent(data.Length);
    }

    public override async Task CloseAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            try { await cts.CancelAsync(); } catch { }
        }

        var udp = Interlocked.Exchange(ref _udp, null);
        if (udp != null)
        {
            try { udp.Close(); } catch { }
            udp.Dispose();
        }

        var readTask = Interlocked.Exchange(ref _readLoopTask, null);
        if (readTask != null)
        {
            try { await readTask; } catch { }
        }

        cts?.Dispose();
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
        if (_parameters.ListenPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(_parameters.ListenPort), "TCP 服务端监听端口必须在 1 至 65535 之间");

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
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            try { await cts.CancelAsync(); } catch { }
        }

        var listener = Interlocked.Exchange(ref _listener, null);
        try { listener?.Stop(); } catch { }

        foreach (var id in _clients.Keys.ToArray())
            DisconnectClient(id);

        var acceptTask = Interlocked.Exchange(ref _acceptLoopTask, null);
        if (acceptTask != null)
        {
            try { await acceptTask; } catch { }
        }

        cts?.Dispose();
        State = ChannelState.Disconnected;
    }
}
