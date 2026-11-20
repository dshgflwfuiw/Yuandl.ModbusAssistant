using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Services;

public sealed class ModbusServerService : IDisposable
{
    private sealed class ModbusErrorCodeException : Exception
    {
        public byte Code { get; }
        public ModbusErrorCodeException(byte code) => Code = code;
    }

    private sealed class TcpClientEntry
    {
        public required TcpServerClientSession Info { get; init; }
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Channel<byte[]> OutboundQueue { get; init; }
        public Task? ReadTask { get; set; }
        public Task? WriteTask { get; set; }
    }

    private readonly ushort[] _holdingRegisters = new ushort[65536];
    private readonly ushort[] _inputRegisters = new ushort[65536];
    private readonly byte[] _coils = new byte[65536];
    private readonly byte[] _discreteInputs = new byte[65536];
    private readonly object _storeLock = new();

    private List<ServerRegisterDefinition> _definitions = new();
    private TcpListener? _tcpListener;
    private readonly ConcurrentDictionary<string, TcpClientEntry> _tcpClients = new();
    private int _nextTcpClientNumber;
    private SerialPort? _serialPort;
    private CancellationTokenSource? _cts;
    private Task? _tcpAcceptTask;
    private Task? _serialReadTask;
    private HashSet<IPAddress> _allowedClientIps = new();

    public bool IsRunning { get; private set; }
    public bool IsSerialLoopbackFallback { get; private set; }
    public ModbusServerTransportMode ServerMode { get; private set; } = ModbusServerTransportMode.Tcp;
    public int Port { get; private set; } = 5020;
    public int Station { get; private set; } = 1;
    public SerialPortParameters SerialParams { get; private set; } = new();
    public ModbusServerSecurityOptions SecurityOptions { get; private set; } = new();
    public string StatusText { get; private set; } = "服务器已停止";

    public bool IsSerialActive => IsRunning && ServerMode is ModbusServerTransportMode.SerialRtu or ModbusServerTransportMode.SerialAscii or ModbusServerTransportMode.TcpAndSerial;

    public IReadOnlyList<TcpServerClientSession> GetTcpClients() =>
        _tcpClients.Values.Select(c => new TcpServerClientSession
        {
            SessionId = c.Info.SessionId,
            RemoteAddress = c.Info.RemoteAddress,
            RemotePort = c.Info.RemotePort,
            ConnectedAt = c.Info.ConnectedAt,
            BytesSent = c.Info.BytesSent,
            BytesReceived = c.Info.BytesReceived
        }).OrderBy(c => c.ConnectedAt).ToList();

    public void DisconnectTcpClient(string sessionId)
    {
        if (_tcpClients.TryRemove(sessionId, out var entry))
        {
            entry.OutboundQueue.Writer.TryComplete();
            try { entry.Cts.Cancel(); } catch { }
            try { entry.Stream.Dispose(); } catch { }
            try { entry.Client.Dispose(); } catch { }
            try { entry.Cts.Dispose(); } catch { }
            NotifyClientsChanged();
        }
    }

    public bool IsSerialEndpointMatch(SerialPortParameters clientSerial) =>
        IsSerialActive &&
        string.Equals(clientSerial.PortName, SerialParams.PortName, StringComparison.OrdinalIgnoreCase) &&
        clientSerial.BaudRate == SerialParams.BaudRate &&
        clientSerial.DataBits == SerialParams.DataBits &&
        clientSerial.Parity == SerialParams.Parity &&
        clientSerial.StopBits == SerialParams.StopBits;

    public byte[]? HandleSerialRequest(SerialPortParameters clientSerial, byte[] bytes)
    {
        if (!IsSerialEndpointMatch(clientSerial)) return null;
        return TryBuildSerialResponse(bytes);
    }

    public event EventHandler? RegistersMutated;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<IReadOnlyList<TcpServerClientSession>>? ClientsChanged;

    public void ConfigureRegisters(IEnumerable<ServerRegisterDefinition> defs)
    {
        var validated = defs.Select(d => new ServerRegisterDefinition
        {
            Area = d.Area,
            Address = d.Address,
            DataType = d.DataType,
            ByteOrder = d.ByteOrder,
            InitialValue = d.InitialValue,
            Note = d.Note,
            CurrentValue = d.CurrentValue
        }).ToList();

        foreach (var def in validated)
            ServerRegisterCodec.Validate(def);
        ServerRegisterCodec.ValidateNoOverlap(validated);

        static bool IsSameSpec(ServerRegisterDefinition a, ServerRegisterDefinition b) =>
            a.Area == b.Area &&
            a.Address == b.Address &&
            a.DataType == b.DataType &&
            a.ByteOrder == b.ByteOrder &&
            a.InitialValue == b.InitialValue;

        lock (_storeLock)
        {
            var previousDefs = _definitions;
            foreach (var oldDef in previousDefs)
            {
                if (!validated.Any(n => IsSameSpec(n, oldDef)))
                    ApplyDefinitionLocked(oldDef, clear: true);
            }

            _definitions = validated
                .OrderBy(d => d.Area.ToString(), StringComparer.Ordinal)
                .ThenBy(d => d.Address)
                .ToList();

            foreach (var def in _definitions)
            {
                if (!previousDefs.Any(oldDef => IsSameSpec(oldDef, def)))
                    ApplyDefinitionLocked(def, clear: false);
            }
        }

        if (IsRunning)
        {
            StatusText = BuildRunningStatusText();
            StatusChanged?.Invoke(this, StatusText);
        }
        RegistersMutated?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToInitialValues()
    {
        lock (_storeLock)
        {
            foreach (var def in _definitions)
                ApplyDefinitionLocked(def, clear: false);
        }
        RegistersMutated?.Invoke(this, EventArgs.Empty);
    }

    public List<ServerRegisterDefinition> GetDefinitionsWithCurrentValues()
    {
        lock (_storeLock)
        {
            return _definitions.Select(d => new ServerRegisterDefinition
            {
                Area = d.Area,
                Address = d.Address,
                DataType = d.DataType,
                ByteOrder = d.ByteOrder,
                InitialValue = d.InitialValue,
                Note = d.Note,
                CurrentValue = GetValueLocked(d)
            }).ToList();
        }
    }

    public void SetRuntimeValue(ServerRegisterArea area, int address, string value)
    {
        lock (_storeLock)
        {
            var def = _definitions.FirstOrDefault(d => d.Area == area && d.Address == address)
                ?? throw new InvalidOperationException($"未找到 {area} 地址 {address} 的数据定义");

            if (area is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput)
            {
                byte bit = ServerRegisterCodec.EncodeBit(value) ? (byte)1 : (byte)0;
                var target = area == ServerRegisterArea.Coil ? _coils : _discreteInputs;
                target[address] = bit;
            }
            else
            {
                var regs = ServerRegisterCodec.EncodeRegisters(value, def.DataType, def.ByteOrder);
                var target = area == ServerRegisterArea.HoldingRegister ? _holdingRegisters : _inputRegisters;
                for (int i = 0; i < regs.Length; i++)
                    target[address + i] = regs[i];
            }
        }
        RegistersMutated?.Invoke(this, EventArgs.Empty);
    }

    private string GetValueLocked(ServerRegisterDefinition def)
    {
        if (def.Area == ServerRegisterArea.Coil)
            return _coils[def.Address] != 0 ? "true" : "false";
        if (def.Area == ServerRegisterArea.DiscreteInput)
            return _discreteInputs[def.Address] != 0 ? "true" : "false";

        var source = def.Area == ServerRegisterArea.HoldingRegister ? _holdingRegisters : _inputRegisters;
        int width = ServerRegisterCodec.GetWidth(def.DataType);
        var slice = new ushort[width];
        for (int i = 0; i < width; i++)
            slice[i] = source[def.Address + i];

        return ServerRegisterCodec.DecodeRegisters(slice, def.DataType, def.ByteOrder);
    }

    private void ApplyDefinitionLocked(ServerRegisterDefinition def, bool clear)
    {
        int width = ServerRegisterCodec.GetWidth(def.DataType);
        if (def.Area is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput)
        {
            var target = def.Area == ServerRegisterArea.Coil ? _coils : _discreteInputs;
            byte val = !clear && ServerRegisterCodec.EncodeBit(def.InitialValue) ? (byte)1 : (byte)0;
            for (int i = 0; i < width; i++)
                target[def.Address + i] = val;
            return;
        }

        var regTarget = def.Area == ServerRegisterArea.HoldingRegister ? _holdingRegisters : _inputRegisters;
        if (clear)
        {
            for (int i = 0; i < width; i++)
                regTarget[def.Address + i] = 0;
            return;
        }

        var encoded = ServerRegisterCodec.EncodeRegisters(def.InitialValue, def.DataType, def.ByteOrder);
        for (int i = 0; i < encoded.Length; i++)
            regTarget[def.Address + i] = encoded[i];
    }

    public async Task StartAsync(
        int port,
        int station = 1,
        ModbusServerTransportMode mode = ModbusServerTransportMode.Tcp,
        SerialPortParameters? serial = null,
        bool allowLoopbackFallback = true,
        ModbusServerSecurityOptions? securityOptions = null)
    {
        if (station is < 1 or > 247)
            throw new ArgumentOutOfRangeException(nameof(station), "站号必须在 1 至 247 之间");

        bool needsTcp = mode is ModbusServerTransportMode.Tcp or ModbusServerTransportMode.TcpAndSerial;
        bool needsSerial = mode is ModbusServerTransportMode.SerialRtu or ModbusServerTransportMode.SerialAscii or ModbusServerTransportMode.TcpAndSerial;

        if (needsTcp && port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "服务器端口必须在 1 至 65535 之间");

        var normalizedSecurity = NormalizeSecurityOptions(securityOptions ?? SecurityOptions);
        var bindIp = needsTcp ? ChannelSecurityHelper.ParseBindAddress(normalizedSecurity.BindAddress) : IPAddress.Loopback;
        var allowedIps = ChannelSecurityHelper.ParseAllowedIps(normalizedSecurity.AllowedClientIps);

        var targetSerial = serial?.Clone() ?? SerialParams.Clone();
        if (needsSerial && string.IsNullOrWhiteSpace(targetSerial.PortName))
            throw new ArgumentException("请选择服务器串口号");

        await StopAsync();

        ServerMode = mode;
        Station = station;
        if (needsTcp) Port = port;
        SerialParams = targetSerial;
        SecurityOptions = normalizedSecurity;
        _allowedClientIps = allowedIps;
        _cts = new CancellationTokenSource();

        try
        {
            if (needsTcp)
            {
                var listener = new TcpListener(bindIp, port);
                listener.Start();
                _tcpListener = listener;
                _tcpAcceptTask = Task.Run(() => TcpAcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
            }

            IsSerialLoopbackFallback = false;
            if (needsSerial)
            {
                try
                {
                    var sp = new SerialPort(targetSerial.PortName, targetSerial.BaudRate, targetSerial.Parity, targetSerial.DataBits, targetSerial.StopBits)
                    {
                        DtrEnable = targetSerial.DtrEnable,
                        RtsEnable = targetSerial.RtsEnable,
                        ReadTimeout = 200,
                        WriteTimeout = 1000
                    };
                    sp.Open();
                    _serialPort = sp;
                    _serialReadTask = Task.Run(() => SerialReadLoopAsync(sp, _cts.Token), CancellationToken.None);
                    AppLogger.Info($"内置 Modbus 从站已打开物理串口 {targetSerial.PortName}");
                }
                catch (Exception ex)
                {
                    if (!allowLoopbackFallback)
                    {
                        AppLogger.Error($"内置 Modbus 从站打开物理串口 {targetSerial.PortName} 失败", ex);
                        throw new InvalidOperationException($"无法打开从站物理串口 {targetSerial.PortName}: {ex.Message}", ex);
                    }

                    IsSerialLoopbackFallback = true;
                    AppLogger.Warn($"内置 Modbus 从站无法打开物理串口 {targetSerial.PortName} ({ex.Message})，已切换至内联仿真模式");
                }
            }

            IsRunning = true;
            StatusText = BuildRunningStatusText();
            StatusChanged?.Invoke(this, StatusText);
            NotifyClientsChanged();
            AppLogger.Info($"内置 Modbus 从站已启动: {StatusText} (MaxClients={SecurityOptions.MaxClients}, IdleTimeout={SecurityOptions.IdleTimeoutMs}ms, MaxFrame={SecurityOptions.MaxFrameLength}B, SendQueue={SecurityOptions.SendQueueLimit})");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    private static ModbusServerSecurityOptions NormalizeSecurityOptions(ModbusServerSecurityOptions source) => new()
    {
        BindAddress = string.IsNullOrWhiteSpace(source.BindAddress) ? "127.0.0.1" : source.BindAddress.Trim(),
        MaxClients = Math.Clamp(source.MaxClients, 1, 256),
        IdleTimeoutMs = Math.Clamp(source.IdleTimeoutMs, 0, 3600000),
        MaxFrameLength = Math.Clamp(source.MaxFrameLength, 16, 4096),
        SendQueueLimit = Math.Clamp(source.SendQueueLimit, 1, 1024),
        AllowedClientIps = source.AllowedClientIps?.Trim() ?? string.Empty
    };

    public async Task StopAsync()
    {
        bool wasRunning = IsRunning || _tcpListener != null || _serialPort != null;
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            try { await cts.CancelAsync(); }
            catch (Exception ex) { AppLogger.Warn($"Modbus 从站取消异常: {ex.Message}"); }
            cts.Dispose();
        }

        try { _tcpListener?.Stop(); } catch { }
        _tcpListener = null;

        foreach (var id in _tcpClients.Keys.ToArray())
            DisconnectTcpClient(id);

        if (_serialPort != null)
        {
            try
            {
                if (_serialPort.IsOpen) _serialPort.Close();
            }
            catch (Exception ex) { AppLogger.Warn($"关闭从站物理串口异常: {ex.Message}"); }
            _serialPort.Dispose();
            _serialPort = null;
        }

        if (_tcpAcceptTask != null)
        {
            try { await _tcpAcceptTask; }
            catch (Exception ex) { AppLogger.Warn($"等待 Modbus 从站 TCP 监听退出异常: {ex.Message}"); }
            _tcpAcceptTask = null;
        }
        if (_serialReadTask != null)
        {
            try { await _serialReadTask; }
            catch (Exception ex) { AppLogger.Warn($"等待 Modbus 从站串口读循环退出异常: {ex.Message}"); }
            _serialReadTask = null;
        }

        IsRunning = false;
        IsSerialLoopbackFallback = false;
        StatusText = "服务器已停止";
        StatusChanged?.Invoke(this, StatusText);
        if (wasRunning)
            AppLogger.Info("内置 Modbus 从站已停止");
    }

    private string BuildRunningStatusText()
    {
        string serialSummary = $"{SerialParams.PortName} ({SerialParams.BaudRate},{SerialParams.DataBits},{SerialParams.Parity},{SerialParams.StopBits})";
        string serialModeTag = IsSerialLoopbackFallback ? "内联仿真串口" : "物理串口";
        int count = _definitions.Count;
        return ServerMode switch
        {
            ModbusServerTransportMode.SerialRtu => $"运行中：{serialModeTag} RTU {serialSummary}，站号 {Station}，数据定义 {count} 项",
            ModbusServerTransportMode.SerialAscii => $"运行中：{serialModeTag} ASCII {serialSummary}，站号 {Station}，数据定义 {count} 项",
            ModbusServerTransportMode.TcpAndSerial => $"运行中：TCP {SecurityOptions.BindAddress}:{Port} + {serialModeTag} {serialSummary}，站号 {Station}，数据定义 {count} 项",
            _ => $"运行中：TCP {SecurityOptions.BindAddress}:{Port}，站号 {Station}，数据定义 {count} 项"
        };
    }

    private void NotifyClientsChanged() => ClientsChanged?.Invoke(this, GetTcpClients());

    private async Task TcpAcceptLoopAsync(TcpListener listener, CancellationToken token)
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

                if (!ChannelSecurityHelper.IsIpAllowed(endpoint?.Address, _allowedClientIps))
                {
                    AppLogger.Warn($"Modbus TCP 从站拒绝非白名单客户端: {remoteIpText}:{remotePort}");
                    try { client.Dispose(); } catch { }
                    continue;
                }

                if (_tcpClients.Count >= SecurityOptions.MaxClients)
                {
                    AppLogger.Warn($"Modbus TCP 从站达到最大客户端上限 ({SecurityOptions.MaxClients})，拒绝 {remoteIpText}:{remotePort}");
                    try { client.Dispose(); } catch { }
                    continue;
                }

                string sessionId = $"M{Interlocked.Increment(ref _nextTcpClientNumber)}";
                var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SecurityOptions.SendQueueLimit)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

                var entry = new TcpClientEntry
                {
                    Info = new TcpServerClientSession
                    {
                        SessionId = sessionId,
                        RemoteAddress = remoteIpText,
                        RemotePort = remotePort,
                        ConnectedAt = DateTime.Now
                    },
                    Client = client,
                    Stream = client.GetStream(),
                    Cts = clientCts,
                    OutboundQueue = queue
                };

                _tcpClients[sessionId] = entry;
                AppLogger.Info($"Modbus TCP 从站接受客户端: {entry.Info.DisplayLabel}");
                NotifyClientsChanged();
                entry.WriteTask = Task.Run(() => TcpWriteLoopAsync(entry), CancellationToken.None);
                entry.ReadTask = Task.Run(() => HandleTcpClientAsync(entry), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                exitReason = "监听已取消";
                break;
            }
            catch (Exception ex)
            {
                exitReason = $"接收异常: {ex.Message}";
                if (!token.IsCancellationRequested)
                    AppLogger.Error("Modbus TCP 从站接收循环异常", ex);
                break;
            }
        }
        AppLogger.Info($"Modbus TCP 从站接收循环退出: {exitReason}");
    }

    private async Task TcpWriteLoopAsync(TcpClientEntry entry)
    {
        try
        {
            await foreach (var payload in entry.OutboundQueue.Reader.ReadAllAsync(entry.Cts.Token))
            {
                await entry.Stream.WriteAsync(payload, entry.Cts.Token);
                await entry.Stream.FlushAsync(entry.Cts.Token);
                entry.Info.BytesSent += payload.Length;
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
                AppLogger.Warn($"Modbus TCP 从站发送至 {entry.Info.SessionId} 失败: {ex.Message}");
                DisconnectTcpClient(entry.Info.SessionId);
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClientEntry entry)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int idleMs = SecurityOptions.IdleTimeoutMs;
        int maxFrameLength = SecurityOptions.MaxFrameLength;
        string disconnectReason = "远端断开";

        try
        {
            var stream = entry.Stream;
            while (!entry.Cts.IsCancellationRequested && entry.Client.Connected)
            {
                int read;
                if (idleMs > 0)
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token);
                    idleCts.CancelAfter(idleMs);
                    try
                    {
                        read = await stream.ReadAsync(chunk, idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!entry.Cts.IsCancellationRequested)
                    {
                        disconnectReason = $"空闲超时 ({idleMs} ms)";
                        AppLogger.Warn($"Modbus TCP 从站客户端 {entry.Info.SessionId} {disconnectReason}");
                        break;
                    }
                }
                else
                {
                    read = await stream.ReadAsync(chunk, entry.Cts.Token);
                }

                if (read == 0)
                    break;

                entry.Info.BytesReceived += read;
                for (int i = 0; i < read; i++) buffer.Add(chunk[i]);

                if (buffer.Count > Math.Max(maxFrameLength * 4, 8192))
                {
                    disconnectReason = $"接收缓冲区超过保护阈值 ({buffer.Count} B)";
                    AppLogger.Warn($"Modbus TCP 从站客户端 {entry.Info.SessionId} {disconnectReason}");
                    return;
                }

                while (buffer.Count >= 7)
                {
                    int protocolId = (buffer[2] << 8) | buffer[3];
                    if (protocolId != 0)
                    {
                        disconnectReason = $"无效协议标识 0x{protocolId:X4}";
                        AppLogger.Warn($"Modbus TCP 从站客户端 {entry.Info.SessionId} {disconnectReason}");
                        return;
                    }

                    int length = (buffer[4] << 8) | buffer[5];
                    int totalLength = 6 + length;
                    if (length is < 2 or > 254 || totalLength > maxFrameLength)
                    {
                        disconnectReason = $"MBAP 帧长度越界 ({totalLength} B，上限 {maxFrameLength} B)";
                        AppLogger.Warn($"Modbus TCP 从站客户端 {entry.Info.SessionId} {disconnectReason}");
                        return;
                    }

                    if (buffer.Count < totalLength) break;

                    var frame = buffer.GetRange(0, totalLength).ToArray();
                    buffer.RemoveRange(0, totalLength);

                    byte unitId = frame[6];
                    var pdu = new byte[frame.Length - 7];
                    Array.Copy(frame, 7, pdu, 0, pdu.Length);
                    var responsePdu = ProcessRequest(unitId, pdu, out bool suppressReply);
                    if (suppressReply || responsePdu.Length == 0)
                        continue;

                    var response = new byte[7 + responsePdu.Length];
                    Array.Copy(frame, 0, response, 0, 4);
                    int respLen = responsePdu.Length + 1;
                    response[4] = (byte)(respLen >> 8);
                    response[5] = (byte)(respLen & 0xFF);
                    response[6] = unitId;
                    Array.Copy(responsePdu, 0, response, 7, responsePdu.Length);

                    if (!entry.OutboundQueue.Writer.TryWrite(response))
                    {
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token);
                        waitCts.CancelAfter(250);
                        if (!await entry.OutboundQueue.Writer.WaitToWriteAsync(waitCts.Token) ||
                            !entry.OutboundQueue.Writer.TryWrite(response))
                        {
                            disconnectReason = $"发送队列拥塞 (上限 {SecurityOptions.SendQueueLimit})";
                            AppLogger.Warn($"Modbus TCP 从站客户端 {entry.Info.SessionId} {disconnectReason}");
                            return;
                        }
                    }
                }
                NotifyClientsChanged();
            }
        }
        catch (OperationCanceledException)
        {
            disconnectReason = "服务端关闭/取消";
        }
        catch (Exception ex)
        {
            disconnectReason = $"会话异常: {ex.Message}";
            if (!entry.Cts.IsCancellationRequested)
                AppLogger.Warn($"Modbus TCP 从站客户端 {entry.Info.SessionId} 异常退出: {ex.Message}");
        }
        finally
        {
            AppLogger.Info($"Modbus TCP 从站客户端 {entry.Info.SessionId} 断开: {disconnectReason}");
            DisconnectTcpClient(entry.Info.SessionId);
        }
    }

    private async Task SerialReadLoopAsync(SerialPort sp, CancellationToken token)
    {
        var buffer = new List<byte>();
        var temp = new byte[512];
        string exitReason = "正常退出";
        while (!token.IsCancellationRequested && sp.IsOpen)
        {
            try
            {
                int read = await sp.BaseStream.ReadAsync(temp, token);
                if (read <= 0) continue;

                for (int i = 0; i < read; i++) buffer.Add(temp[i]);

                while (sp.BytesToRead > 0)
                {
                    int extra = sp.Read(temp, 0, Math.Min(temp.Length, sp.BytesToRead));
                    for (int i = 0; i < extra; i++) buffer.Add(temp[i]);
                }

                while (TryTakeSerialFrame(buffer, out var raw))
                {
                    var reply = TryBuildSerialResponse(raw);
                    if (reply == null) continue;
                    await sp.BaseStream.WriteAsync(reply, token);
                    await sp.BaseStream.FlushAsync(token);
                }

                if (buffer.Count > 4096)
                    buffer.RemoveRange(0, buffer.Count - 4096);
            }
            catch (OperationCanceledException)
            {
                exitReason = "任务已取消";
                break;
            }
            catch (Exception ex)
            {
                buffer.Clear();
                AppLogger.Warn($"Modbus 从站串口读取循环异常: {ex.Message}");
            }
        }
        AppLogger.Info($"Modbus 从站串口读循环退出: {exitReason}");
    }

    private bool TryTakeSerialFrame(List<byte> buffer, out byte[] frame)
    {
        frame = Array.Empty<byte>();
        bool allowRtu = ServerMode is ModbusServerTransportMode.SerialRtu or ModbusServerTransportMode.TcpAndSerial;
        bool allowAscii = ServerMode is ModbusServerTransportMode.SerialAscii or ModbusServerTransportMode.TcpAndSerial;

        while (buffer.Count > 0)
        {
            if (buffer[0] == (byte)':' && allowAscii)
            {
                int terminator = -1;
                for (int i = 1; i < buffer.Count; i++)
                {
                    if (buffer[i - 1] == (byte)'\r' && buffer[i] == (byte)'\n')
                    {
                        terminator = i;
                        break;
                    }
                }

                if (terminator < 0) return false;
                int length = terminator + 1;
                frame = buffer.GetRange(0, length).ToArray();
                buffer.RemoveRange(0, length);
                return true;
            }

            if (!allowRtu)
            {
                if (allowAscii)
                {
                    int colon = buffer.IndexOf((byte)':');
                    if (colon < 0)
                    {
                        buffer.Clear();
                        return false;
                    }
                    buffer.RemoveRange(0, colon);
                    continue;
                }

                buffer.RemoveAt(0);
                continue;
            }

            if (buffer.Count < 4) return false;
            int functionCode = buffer[1];
            int expectedLength;
            if (functionCode is 1 or 2 or 3 or 4 or 5 or 6)
            {
                expectedLength = 8;
            }
            else if (functionCode is 15 or 16)
            {
                if (buffer.Count < 7) return false;
                int qty = (buffer[4] << 8) | buffer[5];
                int bc = buffer[6];
                bool validMultiHeader = functionCode == 15
                    ? qty is >= 1 and <= 1968 && bc == (qty + 7) / 8
                    : qty is >= 1 and <= 123 && bc == qty * 2;
                if (!validMultiHeader)
                {
                    buffer.RemoveAt(0);
                    continue;
                }
                expectedLength = 9 + bc;
            }
            else
            {
                expectedLength = 0;
                int maxProbe = Math.Min(buffer.Count, 64);
                for (int len = 4; len <= maxProbe; len++)
                {
                    if (Crc16.Verify(buffer.GetRange(0, len).ToArray()))
                    {
                        expectedLength = len;
                        break;
                    }
                }

                if (expectedLength == 0)
                {
                    if (buffer.Count >= 8)
                    {
                        buffer.RemoveAt(0);
                        continue;
                    }
                    return false;
                }
            }

            if (buffer.Count < expectedLength) return false;
            var candidate = buffer.GetRange(0, expectedLength).ToArray();
            if (!Crc16.Verify(candidate))
            {
                buffer.RemoveAt(0);
                continue;
            }

            buffer.RemoveRange(0, expectedLength);
            frame = candidate;
            return true;
        }

        return false;
    }

    private byte[]? TryBuildSerialResponse(byte[] bytes)
    {
        bool allowRtu = ServerMode is ModbusServerTransportMode.SerialRtu or ModbusServerTransportMode.TcpAndSerial;
        bool allowAscii = ServerMode is ModbusServerTransportMode.SerialAscii or ModbusServerTransportMode.TcpAndSerial;

        if (allowRtu && bytes.Length >= 4 && Crc16.Verify(bytes))
        {
            byte unitId = bytes[0];
            var pdu = bytes.AsSpan(1, bytes.Length - 3);
            var responsePdu = ProcessRequest(unitId, pdu, out bool suppressReply);
            if (suppressReply || responsePdu.Length == 0)
                return null;

            var body = new byte[1 + responsePdu.Length];
            body[0] = unitId;
            Array.Copy(responsePdu, 0, body, 1, responsePdu.Length);
            ushort crc = Crc16.Compute(body);
            var frame = new byte[body.Length + 2];
            Array.Copy(body, 0, frame, 0, body.Length);
            frame[^2] = (byte)(crc & 0xFF);
            frame[^1] = (byte)(crc >> 8);
            return frame;
        }

        if (allowAscii && bytes.Length >= 7 && bytes[0] == (byte)':' && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n')
        {
            try
            {
                string hexStr = Encoding.ASCII.GetString(bytes, 1, bytes.Length - 3);
                byte[] raw = ByteUtils.HexToBytes(hexStr);
                if (LrcUtils.Verify(raw) && raw.Length >= 3)
                {
                    byte unitId = raw[0];
                    var pdu = raw.AsSpan(1, raw.Length - 2);
                    var responsePdu = ProcessRequest(unitId, pdu, out bool suppressReply);
                    if (suppressReply || responsePdu.Length == 0)
                        return null;

                    var body = new byte[1 + responsePdu.Length];
                    body[0] = unitId;
                    Array.Copy(responsePdu, 0, body, 1, responsePdu.Length);
                    byte lrc = LrcUtils.Compute(body);
                    string outFrame = $":{ByteUtils.BytesToHex(body, string.Empty)}{lrc:X2}\r\n";
                    return Encoding.ASCII.GetBytes(outFrame);
                }
            }
            catch { return null; }
        }

        return null;
    }

    public byte[] ProcessRequest(byte unitId, ReadOnlySpan<byte> request) =>
        ProcessRequest(unitId, request, out _);

    public byte[] ProcessRequest(byte unitId, ReadOnlySpan<byte> request, out bool suppressReply)
    {
        suppressReply = false;
        if (request.Length < 1)
            return new byte[] { 0x80, 0x03 };

        byte fn = request[0];
        bool isBroadcast = unitId == 0;
        if (!isBroadcast && unitId != Station)
            return new byte[] { (byte)(fn | 0x80), 0x0B };

        if (isBroadcast)
        {
            suppressReply = true;
            if (fn is not (5 or 6 or 15 or 16))
                return Array.Empty<byte>();
        }

        bool mutated = false;
        byte[] result;

        lock (_storeLock)
        {
            try
            {
                switch (fn)
                {
                    case 1:
                        result = ReadBitsLocked(fn, request, _coils);
                        break;
                    case 2:
                        result = ReadBitsLocked(fn, request, _discreteInputs);
                        break;
                    case 3:
                        result = ReadRegistersLocked(fn, request, _holdingRegisters);
                        break;
                    case 4:
                        result = ReadRegistersLocked(fn, request, _inputRegisters);
                        break;
                    case 5:
                        result = WriteSingleCoilLocked(request);
                        mutated = true;
                        break;
                    case 6:
                        result = WriteSingleRegisterLocked(request);
                        mutated = true;
                        break;
                    case 15:
                        result = WriteMultipleCoilsLocked(fn, request);
                        mutated = true;
                        break;
                    case 16:
                        result = WriteMultipleRegistersLocked(fn, request);
                        mutated = true;
                        break;
                    default:
                        result = new byte[] { (byte)(fn | 0x80), 0x01 };
                        break;
                }
            }
            catch (ModbusErrorCodeException ex)
            {
                result = isBroadcast
                    ? Array.Empty<byte>()
                    : new byte[] { (byte)(fn | 0x80), ex.Code };
            }
        }

        if (mutated)
            RegistersMutated?.Invoke(this, EventArgs.Empty);

        return isBroadcast ? Array.Empty<byte>() : result;
    }

    private static byte[] ReadBitsLocked(byte fn, ReadOnlySpan<byte> request, byte[] source)
    {
        if (request.Length != 5) throw new ModbusErrorCodeException(0x03);
        int address = (request[1] << 8) | request[2];
        int quantity = (request[3] << 8) | request[4];
        if (quantity is <= 0 or > 2000 || address + quantity > source.Length)
            throw new ModbusErrorCodeException(0x02);

        int byteCount = (quantity + 7) / 8;
        var response = new byte[2 + byteCount];
        response[0] = fn;
        response[1] = (byte)byteCount;
        for (int i = 0; i < quantity; i++)
        {
            if (source[address + i] != 0)
                response[2 + (i / 8)] |= (byte)(1 << (i % 8));
        }
        return response;
    }

    private static byte[] ReadRegistersLocked(byte fn, ReadOnlySpan<byte> request, ushort[] source)
    {
        if (request.Length != 5) throw new ModbusErrorCodeException(0x03);
        int address = (request[1] << 8) | request[2];
        int quantity = (request[3] << 8) | request[4];
        if (quantity is <= 0 or > 125 || address + quantity > source.Length)
            throw new ModbusErrorCodeException(0x02);

        var response = new byte[2 + quantity * 2];
        response[0] = fn;
        response[1] = (byte)(quantity * 2);
        for (int i = 0; i < quantity; i++)
        {
            ushort val = source[address + i];
            response[2 + i * 2] = (byte)(val >> 8);
            response[3 + i * 2] = (byte)(val & 0xFF);
        }
        return response;
    }

    private byte[] WriteSingleCoilLocked(ReadOnlySpan<byte> request)
    {
        if (request.Length != 5) throw new ModbusErrorCodeException(0x03);
        int address = (request[1] << 8) | request[2];
        int value = (request[3] << 8) | request[4];
        if (value is not (0x0000 or 0xFF00)) throw new ModbusErrorCodeException(0x03);
        EnsureWritableLocked(ServerRegisterArea.Coil, address);
        _coils[address] = value == 0xFF00 ? (byte)1 : (byte)0;
        return request.ToArray();
    }

    private byte[] WriteSingleRegisterLocked(ReadOnlySpan<byte> request)
    {
        if (request.Length != 5) throw new ModbusErrorCodeException(0x03);
        int address = (request[1] << 8) | request[2];
        ushort value = (ushort)((request[3] << 8) | request[4]);
        EnsureWritableLocked(ServerRegisterArea.HoldingRegister, address);
        _holdingRegisters[address] = value;
        return request.ToArray();
    }

    private byte[] WriteMultipleCoilsLocked(byte fn, ReadOnlySpan<byte> request)
    {
        if (request.Length < 6) throw new ModbusErrorCodeException(0x03);
        int address = (request[1] << 8) | request[2];
        int quantity = (request[3] << 8) | request[4];
        int byteCount = request[5];
        if (quantity is <= 0 or > 1968 || byteCount != (quantity + 7) / 8 || request.Length != 6 + byteCount)
            throw new ModbusErrorCodeException(0x03);

        for (int i = 0; i < quantity; i++)
            EnsureWritableLocked(ServerRegisterArea.Coil, address + i);

        for (int i = 0; i < quantity; i++)
        {
            bool isOn = (request[6 + (i / 8)] & (1 << (i % 8))) != 0;
            _coils[address + i] = isOn ? (byte)1 : (byte)0;
        }

        return new[] { fn, request[1], request[2], request[3], request[4] };
    }

    private byte[] WriteMultipleRegistersLocked(byte fn, ReadOnlySpan<byte> request)
    {
        if (request.Length < 6) throw new ModbusErrorCodeException(0x03);
        int address = (request[1] << 8) | request[2];
        int quantity = (request[3] << 8) | request[4];
        int byteCount = request[5];
        if (quantity is <= 0 or > 123 || byteCount != quantity * 2 || request.Length != 6 + byteCount)
            throw new ModbusErrorCodeException(0x03);

        for (int i = 0; i < quantity; i++)
            EnsureWritableLocked(ServerRegisterArea.HoldingRegister, address + i);

        for (int i = 0; i < quantity; i++)
            _holdingRegisters[address + i] = (ushort)((request[6 + i * 2] << 8) | request[7 + i * 2]);

        return new[] { fn, request[1], request[2], request[3], request[4] };
    }

    private void EnsureWritableLocked(ServerRegisterArea area, int address)
    {
        if (address is < 0 or > 65535)
            throw new ModbusErrorCodeException(0x02);

        if (_definitions.Count == 0)
            return;

        foreach (var def in _definitions)
        {
            if (def.Area != area) continue;
            int width = ServerRegisterCodec.GetWidth(def.DataType);
            if (address >= def.Address && address < def.Address + width)
                return;
        }

        throw new ModbusErrorCodeException(0x02);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
