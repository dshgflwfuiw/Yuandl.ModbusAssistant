using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.Communication;

internal static class ChannelSecurityHelper
{
    public static IPAddress ParseBindAddress(string? bindAddress)
    {
        string normalized = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress.Trim();
        if (!IPAddress.TryParse(normalized, out var ip))
            throw new ArgumentException($"无效的绑定 IP 地址: {bindAddress}", nameof(bindAddress));

        return ip;
    }

    public static HashSet<IPAddress> ParseAllowedIps(string? raw)
    {
        var result = new HashSet<IPAddress>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var tokens = raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            if (!IPAddress.TryParse(token.Trim(), out var ip))
                throw new ArgumentException($"客户端 IP 白名单格式无效: {token}", nameof(raw));

            result.Add(NormalizeIp(ip));
        }
        return result;
    }

    public static bool IsIpAllowed(IPAddress? remoteAddress, IReadOnlySet<IPAddress> allowlist)
    {
        if (allowlist.Count == 0)
            return true;
        if (remoteAddress == null)
            return false;

        var normalized = NormalizeIp(remoteAddress);
        if (allowlist.Contains(normalized))
            return true;

        if (IPAddress.IsLoopback(normalized) && allowlist.Any(IPAddress.IsLoopback))
            return true;

        return false;
    }

    public static IPAddress NormalizeIp(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

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
            AppLogger.Info($"串口通道已打开: {_parameters.PortName} ({_parameters.BaudRate})");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            AppLogger.Error($"串口 {_parameters.PortName} 打开失败", ex);
            OnError($"串口打开失败: {ex.Message}");
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        string reason = "正常退出";
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
                reason = "任务已取消";
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    reason = $"读取异常: {ex.Message}";
                    AppLogger.Error($"串口 {_parameters.PortName} 读取循环异常", ex);
                    OnError($"串口读取异常: {ex.Message}");
                    State = ChannelState.Faulted;
                }
                else
                {
                    reason = "关闭中取消";
                }
                break;
            }
        }
        AppLogger.Info($"串口读取循环结束 ({_parameters.PortName}): {reason}");
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
            try { await cts.CancelAsync(); }
            catch (Exception ex) { AppLogger.Warn($"串口取消令牌关闭异常: {ex.Message}"); }
        }

        var port = Interlocked.Exchange(ref _port, null);
        if (port != null)
        {
            try
            {
                if (port.IsOpen) port.Close();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"关闭串口句柄异常: {ex.Message}");
            }
            port.Dispose();
        }

        var readTask = Interlocked.Exchange(ref _readLoopTask, null);
        if (readTask != null)
        {
            try { await readTask; }
            catch (Exception ex) { AppLogger.Warn($"等待串口读循环退出异常: {ex.Message}"); }
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
        AppLogger.Info($"虚拟环回串口已打开: {_parameters.PortName} ({_parameters.BaudRate})");
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
                try
                {
                    await Task.Delay(5, CancellationToken.None);
                    if (State == ChannelState.Connected)
                        OnDataReceived(reply);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("虚拟环回串口应答投递异常", ex);
                }
            }, CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public override Task CloseAsync()
    {
        State = ChannelState.Disconnected;
        AppLogger.Info($"虚拟环回串口已关闭: {_parameters.PortName}");
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
            AppLogger.Info($"TCP 客户端连接成功: {_parameters.Host}:{_parameters.Port}");
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }
            AppLogger.Error($"TCP 客户端连接失败 ({_parameters.Host}:{_parameters.Port})", ex);
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
            {
                AppLogger.Info($"TCP 客户端监督循环退出: {_parameters.Host}:{_parameters.Port} (主动关闭或已取消)");
                break;
            }

            CleanupSocket();

            if (!_parameters.AutoReconnect)
            {
                AppLogger.Info($"TCP 客户端已断开且未启用自动重连: {_parameters.Host}:{_parameters.Port}");
                State = ChannelState.Disconnected;
                break;
            }

            State = ChannelState.Reconnecting;
            AppLogger.Warn($"TCP 客户端开始自动重连: {_parameters.Host}:{_parameters.Port} (间隔 {reconnectIntervalMs} ms)");
            while (!sessionToken.IsCancellationRequested && !_manualClosing)
            {
                try
                {
                    await Task.Delay(reconnectIntervalMs, sessionToken);
                    if (sessionToken.IsCancellationRequested || _manualClosing)
                        return;

                    await ConnectSocketAsync(CancellationToken.None, sessionToken);
                    break;
                }
                catch (OperationCanceledException)
                {
                    AppLogger.Info($"TCP 客户端重连循环已取消: {_parameters.Host}:{_parameters.Port}");
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"TCP 客户端重连失败 ({_parameters.Host}:{_parameters.Port}): {ex.Message}");
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
                    {
                        AppLogger.Warn($"TCP 远端关闭了连接: {_parameters.Host}:{_parameters.Port}");
                        OnError("TCP 远端连接已断开");
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
                    AppLogger.Error($"TCP 读取异常 ({_parameters.Host}:{_parameters.Port})", ex);
                    OnError($"TCP 读取异常: {ex.Message}");
                }
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
            try { await cts.CancelAsync(); }
            catch (Exception ex) { AppLogger.Warn($"TCP 客户端取消异常: {ex.Message}"); }
        }

        CleanupSocket();

        var supervisor = Interlocked.Exchange(ref _supervisorTask, null);
        if (supervisor != null)
        {
            try { await supervisor; }
            catch (Exception ex) { AppLogger.Warn($"TCP 客户端等待监督退出异常: {ex.Message}"); }
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
            AppLogger.Info($"UDP 通道已打开: -> {_parameters.RemoteHost}:{_parameters.RemotePort} (本地端口 {_parameters.LocalPort})");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            AppLogger.Error("UDP 初始化失败", ex);
            OnError($"UDP 初始化失败: {ex.Message}");
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        string reason = "正常退出";
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
                reason = "任务已取消";
                break;
            }
            catch (ObjectDisposedException)
            {
                reason = "套接字已释放";
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    reason = $"接收异常: {ex.Message}";
                    AppLogger.Error("UDP 接收循环异常", ex);
                    OnError($"UDP 接收异常: {ex.Message}");
                }
                break;
            }
        }
        AppLogger.Info($"UDP 接收循环退出: {reason}");
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
            try { await cts.CancelAsync(); }
            catch (Exception ex) { AppLogger.Warn($"UDP 取消异常: {ex.Message}"); }
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
            try { await readTask; }
            catch (Exception ex) { AppLogger.Warn($"UDP 等待读循环退出异常: {ex.Message}"); }
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
        public required CancellationTokenSource Cts { get; init; }
        public required Channel<byte[]> OutboundQueue { get; init; }
        public Task? ReadTask { get; set; }
        public Task? WriteTask { get; set; }
    }

    private readonly TcpServerParameters _parameters;
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private int _nextClientNumber;
    private HashSet<IPAddress> _allowedIps = new();
    private IPAddress _bindIp = IPAddress.Loopback;

    public TcpServerChannel(TcpServerParameters parameters)
    {
        _parameters = parameters.Clone();
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

        _bindIp = ChannelSecurityHelper.ParseBindAddress(_parameters.BindAddress);
        _allowedIps = ChannelSecurityHelper.ParseAllowedIps(_parameters.AllowedClientIps);

        State = ChannelState.Connecting;
        try
        {
            var listener = new TcpListener(_bindIp, _parameters.ListenPort);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
            State = ChannelState.Connected;
            NotifyClientsChanged();
            AppLogger.Info($"TCP 服务端启动监听: {_bindIp}:{_parameters.ListenPort} (MaxClients={_parameters.MaxClients}, IdleTimeoutMs={_parameters.IdleTimeoutMs}, MaxFrame={_parameters.MaxFrameLength}, SendQueue={_parameters.SendQueueLimit})");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            AppLogger.Error($"TCP 服务端监听启动失败 ({_bindIp}:{_parameters.ListenPort})", ex);
            OnError($"TCP 服务端监听启动失败: {ex.Message}");
            throw;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        string exitReason = "正常停止";
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                string remoteIpText = endpoint?.Address.ToString() ?? "unknown";
                int remotePort = endpoint?.Port ?? 0;

                if (!ChannelSecurityHelper.IsIpAllowed(endpoint?.Address, _allowedIps))
                {
                    AppLogger.Warn($"TCP 服务端拒绝非白名单客户端: {remoteIpText}:{remotePort}");
                    OnError($"已拒绝非白名单客户端 {remoteIpText}:{remotePort}");
                    try { client.Dispose(); } catch { }
                    continue;
                }

                if (_clients.Count >= _parameters.MaxClients)
                {
                    AppLogger.Warn($"TCP 服务端已达最大连接数 ({_parameters.MaxClients})，拒绝 {remoteIpText}:{remotePort}");
                    OnError($"已达最大客户端上限 ({_parameters.MaxClients})，拒绝 {remoteIpText}:{remotePort}");
                    try { client.Dispose(); } catch { }
                    continue;
                }

                string sessionId = $"C{Interlocked.Increment(ref _nextClientNumber)}";
                var info = new TcpServerClientSession
                {
                    SessionId = sessionId,
                    RemoteAddress = remoteIpText,
                    RemotePort = remotePort,
                    ConnectedAt = DateTime.Now
                };

                var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_parameters.SendQueueLimit)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

                var entry = new ClientEntry
                {
                    Info = info,
                    Client = client,
                    Stream = client.GetStream(),
                    Cts = clientCts,
                    OutboundQueue = queue
                };

                _clients[sessionId] = entry;
                AppLogger.Info($"TCP 服务端接受客户端 {info.DisplayLabel}");
                NotifyClientsChanged();
                entry.WriteTask = Task.Run(() => ClientWriteLoopAsync(entry), CancellationToken.None);
                entry.ReadTask = Task.Run(() => ClientReadLoopAsync(entry), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                exitReason = "监听已取消";
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    exitReason = $"接收异常: {ex.Message}";
                    AppLogger.Error("TCP 服务端接收连接异常", ex);
                    OnError($"TCP 服务端接收连接异常: {ex.Message}");
                }
                break;
            }
        }
        AppLogger.Info($"TCP 服务端接收循环退出: {exitReason}");
    }

    private async Task ClientReadLoopAsync(ClientEntry entry)
    {
        int maxFrame = _parameters.MaxFrameLength;
        int idleMs = _parameters.IdleTimeoutMs;
        var buffer = new byte[maxFrame + 1];
        string disconnectReason = "远端主动断开";

        try
        {
            while (!entry.Cts.IsCancellationRequested && entry.Client.Connected)
            {
                int read;
                if (idleMs > 0)
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token);
                    idleCts.CancelAfter(idleMs);
                    try
                    {
                        read = await entry.Stream.ReadAsync(buffer, idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!entry.Cts.IsCancellationRequested)
                    {
                        disconnectReason = $"空闲超时 ({idleMs} ms)";
                        AppLogger.Warn($"TCP 服务端客户端 {entry.Info.SessionId} {disconnectReason}，关闭会话");
                        OnError($"客户端 {entry.Info.SessionId} 空闲超时断开");
                        break;
                    }
                }
                else
                {
                    read = await entry.Stream.ReadAsync(buffer, entry.Cts.Token);
                }

                if (read == 0)
                    break;

                if (read > maxFrame)
                {
                    disconnectReason = $"单包超过上限 ({read} > {maxFrame} 字节)";
                    AppLogger.Warn($"TCP 服务端客户端 {entry.Info.SessionId} {disconnectReason}，断开会话");
                    OnError($"客户端 {entry.Info.SessionId} 发送数据超过单包上限 ({maxFrame} B)");
                    break;
                }

                var packet = new byte[read];
                Buffer.BlockCopy(buffer, 0, packet, 0, read);
                entry.Info.BytesReceived += read;
                NotifyClientsChanged();
                OnDataReceived(packet, entry.Info.SessionId, entry.Info.DisplayLabel);
            }
        }
        catch (OperationCanceledException)
        {
            disconnectReason = "服务端关闭/会话被取消";
        }
        catch (Exception ex)
        {
            if (!entry.Cts.IsCancellationRequested)
            {
                disconnectReason = $"读取异常: {ex.Message}";
                AppLogger.Warn($"TCP 服务端客户端 {entry.Info.SessionId} 读取异常: {ex.Message}");
                OnError($"客户端 {entry.Info.SessionId} 读取异常: {ex.Message}");
            }
        }
        finally
        {
            AppLogger.Info($"TCP 服务端客户端 {entry.Info.SessionId} 会话结束: {disconnectReason}");
            DisconnectClient(entry.Info.SessionId);
        }
    }

    private async Task ClientWriteLoopAsync(ClientEntry entry)
    {
        try
        {
            await foreach (var payload in entry.OutboundQueue.Reader.ReadAllAsync(entry.Cts.Token))
            {
                await entry.Stream.WriteAsync(payload, entry.Cts.Token);
                await entry.Stream.FlushAsync(entry.Cts.Token);
                entry.Info.BytesSent += payload.Length;
                RecordSent(payload.Length);
                NotifyClientsChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect/close
        }
        catch (Exception ex)
        {
            if (!entry.Cts.IsCancellationRequested)
            {
                AppLogger.Warn($"向 TCP 客户端 {entry.Info.SessionId} 发送失败: {ex.Message}");
                OnError($"向 {entry.Info.SessionId} 发送失败: {ex.Message}");
                DisconnectClient(entry.Info.SessionId);
            }
        }
    }

    public void DisconnectClient(string sessionId)
    {
        if (_clients.TryRemove(sessionId, out var entry))
        {
            entry.OutboundQueue.Writer.TryComplete();
            try { entry.Cts.Cancel(); } catch { }
            try { entry.Stream.Dispose(); } catch { }
            try { entry.Client.Dispose(); } catch { }
            try { entry.Cts.Dispose(); } catch { }
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
            var copy = data.ToArray();
            if (target.OutboundQueue.Writer.TryWrite(copy))
                continue;

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, target.Cts.Token);
            waitCts.CancelAfter(250);
            try
            {
                if (await target.OutboundQueue.Writer.WaitToWriteAsync(waitCts.Token) &&
                    target.OutboundQueue.Writer.TryWrite(copy))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // fall through to queue saturation handling
            }

            AppLogger.Warn($"TCP 服务端客户端 {target.Info.SessionId} 发送队列已满 (上限 {_parameters.SendQueueLimit})，断开慢客户端");
            OnError($"客户端 {target.Info.SessionId} 发送队列已满 ({_parameters.SendQueueLimit})，已断开");
            DisconnectClient(target.Info.SessionId);

            if (!string.IsNullOrWhiteSpace(targetSessionId))
                throw new InvalidOperationException($"客户端会话 {target.Info.SessionId} 发送队列拥塞并已断开");
        }
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
            try { await cts.CancelAsync(); }
            catch (Exception ex) { AppLogger.Warn($"TCP 服务端关闭取消异常: {ex.Message}"); }
        }

        var listener = Interlocked.Exchange(ref _listener, null);
        try { listener?.Stop(); } catch { }

        foreach (var id in _clients.Keys.ToArray())
            DisconnectClient(id);

        var acceptTask = Interlocked.Exchange(ref _acceptLoopTask, null);
        if (acceptTask != null)
        {
            try { await acceptTask; }
            catch (Exception ex) { AppLogger.Warn($"TCP 服务端等待监听退出异常: {ex.Message}"); }
        }

        cts?.Dispose();
        State = ChannelState.Disconnected;
        AppLogger.Info("TCP 服务端监听已关闭");
    }
}
