using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Services;

public sealed class CommunicationService : IDisposable
{
    private sealed class PendingTcpTransaction
    {
        public required ushort WireTxId { get; init; }
        public required ushort OriginalTxId { get; init; }
        public required byte ExpectedUnitId { get; init; }
        public required byte ExpectedFunctionCode { get; init; }
        public required TaskCompletionSource<byte[]> Completion { get; init; }
    }

    private readonly SemaphoreSlim _serialExchangeLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, PendingTcpTransaction> _pendingTcpTransactions = new();
    private readonly List<byte> _tcpRxAccumulator = new();
    private readonly object _tcpAccumulatorLock = new();
    private int _wireTxSequence;
    private IChannel? _activeChannel;

    public ChannelState State => _activeChannel?.State ?? ChannelState.Disconnected;
    public CommunicationType? ActiveType => _activeChannel?.Type;
    public ChannelStatistics Statistics => _activeChannel?.Statistics ?? new ChannelStatistics();
    public bool IsVirtualLoopback => _activeChannel is VirtualLoopbackSerialChannel;
    public string ActiveEndpointLabel { get; private set; } = string.Empty;

    public IReadOnlyList<TcpServerClientSession> ConnectedClients =>
        _activeChannel is TcpServerChannel tcpServer ? tcpServer.GetClients() : Array.Empty<TcpServerClientSession>();

    public event EventHandler<ChannelState>? StateChanged;
    public event EventHandler<ChannelDataReceivedEventArgs>? DataReceived;
    public event EventHandler<TrafficEventArgs>? TrafficCaptured;
    public event EventHandler<IReadOnlyList<TcpServerClientSession>>? ClientsChanged;
    public event EventHandler<string>? ErrorOccurred;

    public async Task ConnectSerialAsync(
        SerialPortParameters parameters,
        ModbusServerService? modbusServer = null,
        bool allowVirtualLoopback = true)
    {
        await DisconnectAsync();

        if (allowVirtualLoopback &&
            modbusServer != null &&
            modbusServer.IsSerialLoopbackFallback &&
            modbusServer.IsSerialEndpointMatch(parameters))
        {
            ActiveEndpointLabel = $"虚拟环回串口 {parameters.PortName} ({parameters.BaudRate})";
            AppLogger.Info($"串口通道使用内联虚拟环回: {ActiveEndpointLabel}");
            await OpenChannelAsync(new VirtualLoopbackSerialChannel(parameters, modbusServer.HandleSerialRequest));
            return;
        }

        try
        {
            var channel = new SerialPortChannel(parameters);
            await OpenChannelAsync(channel);
            ActiveEndpointLabel = $"物理串口 {parameters.PortName} ({parameters.BaudRate})";
            AppLogger.Info($"已打开物理串口: {ActiveEndpointLabel}");
        }
        catch (Exception ex)
        {
            if (allowVirtualLoopback && modbusServer != null && modbusServer.IsSerialEndpointMatch(parameters))
            {
                ActiveEndpointLabel = $"虚拟环回串口 {parameters.PortName} ({parameters.BaudRate})";
                AppLogger.Warn($"物理串口 {parameters.PortName} 打开失败 ({ex.Message})，已切换至内置仿真环回通道");
                await OpenChannelAsync(new VirtualLoopbackSerialChannel(parameters, modbusServer.HandleSerialRequest));
                return;
            }

            AppLogger.Error($"串口 {parameters.PortName} 打开失败", ex);
            throw;
        }
    }

    public async Task ConnectTcpClientAsync(TcpClientParameters parameters)
    {
        await DisconnectAsync();
        await OpenChannelAsync(new TcpClientChannel(parameters));
        ActiveEndpointLabel = $"TCP {parameters.Host}:{parameters.Port}";
        AppLogger.Info($"TCP 客户端已连接: {ActiveEndpointLabel}");
    }

    public async Task ConnectUdpAsync(UdpParameters parameters)
    {
        await DisconnectAsync();
        await OpenChannelAsync(new UdpChannel(parameters));
        ActiveEndpointLabel = $"UDP {parameters.RemoteHost}:{parameters.RemotePort}";
        AppLogger.Info($"UDP 通道已就绪: {ActiveEndpointLabel}");
    }

    public async Task StartTcpServerAsync(TcpServerParameters parameters)
    {
        await DisconnectAsync();
        await OpenChannelAsync(new TcpServerChannel(parameters));
        ActiveEndpointLabel = $"TCP 服务端 {parameters.BindAddress}:{parameters.ListenPort}";
        AppLogger.Info($"串口/TCP 透明服务端监听已启动: {ActiveEndpointLabel}");
    }

    private async Task OpenChannelAsync(IChannel channel)
    {
        lock (_tcpAccumulatorLock)
            _tcpRxAccumulator.Clear();

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
        CancelAllPendingTcpTransactions("通讯连接已断开");
        lock (_tcpAccumulatorLock)
            _tcpRxAccumulator.Clear();

        var ch = _activeChannel;
        _activeChannel = null;
        ActiveEndpointLabel = string.Empty;
        if (ch != null)
        {
            ch.StateChanged -= OnChannelStateChanged;
            ch.DataReceived -= OnChannelDataReceived;
            ch.ErrorOccurred -= OnChannelErrorOccurred;
            if (ch is TcpServerChannel srv)
                srv.ClientsChanged -= OnServerClientsChanged;

            await ch.CloseAsync();
            ch.Dispose();
            AppLogger.Info($"通讯通道已断开 ({ch.Type})");
        }
        StateChanged?.Invoke(this, ChannelState.Disconnected);
        ClientsChanged?.Invoke(this, Array.Empty<TcpServerClientSession>());
    }

    public async Task SendAsync(byte[] data, string? targetSessionId = null, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var channel = _activeChannel;
            if (channel == null || channel.State != ChannelState.Connected)
                throw new InvalidOperationException("当前无活跃连接通道");

            await channel.SendAsync(data, targetSessionId, cancellationToken);
            TrafficCaptured?.Invoke(this, new TrafficEventArgs
            {
                Direction = "TX",
                Bytes = data,
                Hex = ByteUtils.BytesToHex(data),
                Timestamp = DateTime.Now,
                ChannelType = channel.Type,
                SessionId = targetSessionId,
                SessionLabel = targetSessionId != null ? $"-> {targetSessionId}" : null
            });
        }
        finally
        {
            _sendLock.Release();
        }
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
        if (request == null || request.Length == 0)
            throw new ArgumentException("Modbus 请求报文不能为空", nameof(request));

        var activeChannel = _activeChannel;
        if (activeChannel == null || activeChannel.State != ChannelState.Connected)
            throw new InvalidOperationException("请先在通讯连接页建立连接");

        int normalizedTimeoutMs = Math.Clamp(timeoutMs, 100, 30000);
        int normalizedRetries = Math.Clamp(retries, 0, 10);

        bool hasMeta = TryExtractRequestMeta(request, transport, out byte expectedUnitId, out byte expectedFn, out ushort? expectedTxId);
        if (hasMeta && expectedUnitId == 0 && expectedFn is 5 or 6 or 15 or 16)
        {
            await _serialExchangeLock.WaitAsync(cancellationToken);
            try
            {
                await SendAsync(request, null, cancellationToken);
                await Task.Delay(35, cancellationToken);
                return Array.Empty<byte>();
            }
            finally
            {
                _serialExchangeLock.Release();
            }
        }

        // TCP client + Modbus TCP transport supports concurrent in-flight transactions keyed by TxId
        if (activeChannel is TcpClientChannel && transport == ModbusTransport.Tcp && hasMeta && expectedTxId.HasValue)
        {
            return await ExchangeTcpMultiplexedAsync(
                request,
                expectedTxId.Value,
                expectedUnitId,
                expectedFn,
                normalizedTimeoutMs,
                normalizedRetries,
                cancellationToken);
        }

        // Serial / UDP / RTU / ASCII remain strictly serialized per physical bus semantics
        return await ExchangeSerializedAsync(
            request,
            transport,
            hasMeta ? expectedTxId : null,
            hasMeta ? expectedUnitId : null,
            hasMeta ? expectedFn : null,
            normalizedTimeoutMs,
            normalizedRetries,
            cancellationToken);
    }

    private async Task<byte[]> ExchangeTcpMultiplexedAsync(
        byte[] request,
        ushort originalTxId,
        byte expectedUnitId,
        byte expectedFn,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        int maxAttempts = Math.Max(1, retries + 1);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var channel = _activeChannel;
            if (channel == null || channel.State != ChannelState.Connected)
                throw new InvalidOperationException("通讯连接已断开");

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            ushort wireTxId = AllocateUniqueWireTxId(originalTxId, expectedUnitId, expectedFn, tcs);
            byte[] wireFrame = request;
            if (wireTxId != originalTxId)
            {
                wireFrame = request.ToArray();
                wireFrame[0] = (byte)(wireTxId >> 8);
                wireFrame[1] = (byte)(wireTxId & 0xFF);
            }

            try
            {
                await SendAsync(wireFrame, null, cancellationToken);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeoutMs);
                using var reg = timeoutCts.Token.Register(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        tcs.TrySetCanceled(cancellationToken);
                    else
                        tcs.TrySetException(new TimeoutException("Modbus TCP 响应超时"));
                });

                byte[] response = await tcs.Task;
                if (wireTxId != originalTxId && response.Length >= 2)
                {
                    response[0] = (byte)(originalTxId >> 8);
                    response[1] = (byte)(originalTxId & 0xFF);
                }
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                AppLogger.Warn($"Modbus TCP 事务 #{wireTxId} 尝试 {attempt + 1}/{maxAttempts} 失败: {ex.Message}");
            }
            finally
            {
                _pendingTcpTransactions.TryRemove(wireTxId, out _);
            }
        }

        throw lastException ?? new TimeoutException("Modbus TCP 请求未收到响应");
    }

    private ushort AllocateUniqueWireTxId(
        ushort preferredTxId,
        byte expectedUnitId,
        byte expectedFn,
        TaskCompletionSource<byte[]> completion)
    {
        var candidate = new PendingTcpTransaction
        {
            WireTxId = preferredTxId,
            OriginalTxId = preferredTxId,
            ExpectedUnitId = expectedUnitId,
            ExpectedFunctionCode = expectedFn,
            Completion = completion
        };

        if (_pendingTcpTransactions.TryAdd(preferredTxId, candidate))
            return preferredTxId;

        for (int i = 0; i < 65535; i++)
        {
            ushort nextId = (ushort)((Interlocked.Increment(ref _wireTxSequence) & 0xFFFF) == 0
                ? Interlocked.Increment(ref _wireTxSequence)
                : _wireTxSequence);
            var allocated = new PendingTcpTransaction
            {
                WireTxId = nextId,
                OriginalTxId = preferredTxId,
                ExpectedUnitId = expectedUnitId,
                ExpectedFunctionCode = expectedFn,
                Completion = completion
            };
            if (_pendingTcpTransactions.TryAdd(nextId, allocated))
                return nextId;
        }

        throw new InvalidOperationException("TCP 并发事务表已满");
    }

    private async Task<byte[]> ExchangeSerializedAsync(
        byte[] request,
        ModbusTransport transport,
        ushort? expectedTxId,
        byte? expectedUnitId,
        byte? expectedFn,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        await _serialExchangeLock.WaitAsync(cancellationToken);
        try
        {
            Exception? lastException = null;
            int maxAttempts = Math.Max(1, retries + 1);

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var channel = _activeChannel;
                if (channel == null || channel.State != ChannelState.Connected)
                    throw new InvalidOperationException("通讯连接已断开");

                var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                var accumulator = new List<byte>();

                void Handler(object? _, ChannelDataReceivedEventArgs e)
                {
                    lock (accumulator)
                    {
                        accumulator.AddRange(e.Bytes);
                        if (accumulator.Count > 16384)
                            accumulator.RemoveRange(0, accumulator.Count - 16384);

                        if (TryExtractMatchingFrame(
                                accumulator,
                                transport,
                                expectedTxId,
                                expectedUnitId,
                                expectedFn,
                                out var matched))
                        {
                            tcs.TrySetResult(matched);
                        }
                    }
                }

                channel.DataReceived += Handler;
                try
                {
                    await SendAsync(request, null, cancellationToken);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(timeoutMs);
                    using var reg = timeoutCts.Token.Register(() =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                            tcs.TrySetCanceled(cancellationToken);
                        else
                            tcs.TrySetException(new TimeoutException("Modbus 响应超时"));
                    });

                    return await tcs.Task;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    lastException = ex;
                }
                finally
                {
                    channel.DataReceived -= Handler;
                }
            }

            throw lastException ?? new TimeoutException("Modbus 请求未收到响应");
        }
        finally
        {
            _serialExchangeLock.Release();
        }
    }

    private void DispatchPendingTcpFrames(byte[] bytes)
    {
        if (_pendingTcpTransactions.IsEmpty)
            return;

        lock (_tcpAccumulatorLock)
        {
            _tcpRxAccumulator.AddRange(bytes);
            if (_tcpRxAccumulator.Count > 32768)
                _tcpRxAccumulator.RemoveRange(0, _tcpRxAccumulator.Count - 32768);

            while (_tcpRxAccumulator.Count >= 7)
            {
                int protocolId = (_tcpRxAccumulator[2] << 8) | _tcpRxAccumulator[3];
                int declaredLength = (_tcpRxAccumulator[4] << 8) | _tcpRxAccumulator[5];
                if (protocolId != 0 || declaredLength is < 2 or > 260)
                {
                    _tcpRxAccumulator.RemoveAt(0);
                    continue;
                }

                int totalLength = 6 + declaredLength;
                if (_tcpRxAccumulator.Count < totalLength)
                    break;

                var frame = _tcpRxAccumulator.GetRange(0, totalLength).ToArray();
                _tcpRxAccumulator.RemoveRange(0, totalLength);

                if (frame.Length < 8)
                    continue;

                ushort respTxId = (ushort)((frame[0] << 8) | frame[1]);
                byte respUnitId = frame[6];
                byte respFn = frame[7];

                if (_pendingTcpTransactions.TryGetValue(respTxId, out var pending))
                {
                    if (respUnitId == pending.ExpectedUnitId &&
                        (respFn == pending.ExpectedFunctionCode || respFn == (byte)(pending.ExpectedFunctionCode | 0x80)))
                    {
                        _pendingTcpTransactions.TryRemove(respTxId, out _);
                        pending.Completion.TrySetResult(frame);
                    }
                    else
                    {
                        AppLogger.Warn($"丢弃参数失配 Modbus TCP 帧: Tx={respTxId}, Unit={respUnitId}/{pending.ExpectedUnitId}, Fn=0x{respFn:X2}/0x{pending.ExpectedFunctionCode:X2}");
                    }
                }
            }
        }
    }

    private void CancelAllPendingTcpTransactions(string reason)
    {
        foreach (var key in _pendingTcpTransactions.Keys.ToArray())
        {
            if (_pendingTcpTransactions.TryRemove(key, out var pending))
                pending.Completion.TrySetException(new InvalidOperationException(reason));
        }
    }

    private static bool TryExtractRequestMeta(
        byte[] request,
        ModbusTransport transport,
        out byte unitId,
        out byte fn,
        out ushort? transactionId)
    {
        unitId = 0;
        fn = 0;
        transactionId = null;

        if (transport == ModbusTransport.Tcp)
        {
            if (request.Length < 8) return false;
            transactionId = (ushort)((request[0] << 8) | request[1]);
            unitId = request[6];
            fn = request[7];
            return true;
        }

        if (transport == ModbusTransport.Ascii)
        {
            if (request.Length < 7 || request[0] != (byte)':') return false;
            try
            {
                string hex = Encoding.ASCII.GetString(request, 1, request.Length - 3);
                var raw = ByteUtils.HexToBytes(hex);
                if (raw.Length < 3) return false;
                unitId = raw[0];
                fn = raw[1];
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (request.Length < 4) return false;
        unitId = request[0];
        fn = request[1];
        return true;
    }

    private static bool TryExtractMatchingFrame(
        List<byte> buffer,
        ModbusTransport transport,
        ushort? expectedTxId,
        byte? expectedUnitId,
        byte? expectedFn,
        out byte[] matchedFrame)
    {
        matchedFrame = Array.Empty<byte>();

        if (transport == ModbusTransport.Tcp)
        {
            while (buffer.Count >= 7)
            {
                int protocolId = (buffer[2] << 8) | buffer[3];
                int declaredLength = (buffer[4] << 8) | buffer[5];
                if (protocolId != 0 || declaredLength is < 2 or > 260)
                {
                    buffer.RemoveAt(0);
                    continue;
                }

                int totalLength = 6 + declaredLength;
                if (buffer.Count < totalLength)
                    return false;

                var candidate = buffer.GetRange(0, totalLength).ToArray();
                buffer.RemoveRange(0, totalLength);

                if (candidate.Length < 8)
                    continue;

                ushort respTxId = (ushort)((candidate[0] << 8) | candidate[1]);
                byte respUnitId = candidate[6];
                byte respFn = candidate[7];

                if (expectedTxId.HasValue && respTxId != expectedTxId.Value)
                {
                    AppLogger.Warn($"丢弃失配事务号 Modbus TCP 帧: 期望 {expectedTxId.Value}, 实际 {respTxId}");
                    continue;
                }
                if (expectedUnitId.HasValue && respUnitId != expectedUnitId.Value)
                {
                    AppLogger.Warn($"丢弃失配从站号 Modbus TCP 帧: 期望 {expectedUnitId.Value}, 实际 {respUnitId}");
                    continue;
                }
                if (expectedFn.HasValue && respFn != expectedFn.Value && respFn != (byte)(expectedFn.Value | 0x80))
                {
                    AppLogger.Warn($"丢弃失配功能码 Modbus TCP 帧: 期望 0x{expectedFn.Value:X2}, 实际 0x{respFn:X2}");
                    continue;
                }

                matchedFrame = candidate;
                return true;
            }
            return false;
        }

        if (transport == ModbusTransport.Ascii)
        {
            while (buffer.Count > 0)
            {
                int start = buffer.IndexOf((byte)':');
                if (start < 0)
                {
                    buffer.Clear();
                    return false;
                }
                if (start > 0)
                    buffer.RemoveRange(0, start);

                int crlfEnd = -1;
                for (int i = 1; i < buffer.Count; i++)
                {
                    if (buffer[i - 1] == (byte)'\r' && buffer[i] == (byte)'\n')
                    {
                        crlfEnd = i;
                        break;
                    }
                }
                if (crlfEnd < 0)
                    return false;

                int frameLen = crlfEnd + 1;
                var candidate = buffer.GetRange(0, frameLen).ToArray();
                buffer.RemoveRange(0, frameLen);

                try
                {
                    var parsed = ModbusFrame.ParseResponse(candidate, ModbusTransport.Ascii);
                    if (expectedUnitId.HasValue && parsed.UnitId != expectedUnitId.Value) continue;
                    if (expectedFn.HasValue && parsed.FunctionCode != expectedFn.Value && parsed.FunctionCode != (byte)(expectedFn.Value | 0x80)) continue;
                    matchedFrame = candidate;
                    return true;
                }
                catch
                {
                    // discard malformed frame and inspect next
                }
            }
            return false;
        }

        while (buffer.Count >= 4)
        {
            byte respFn = buffer[1];
            int expectedLen;
            if ((respFn & 0x80) != 0)
            {
                expectedLen = 5;
            }
            else if (respFn is 1 or 2 or 3 or 4)
            {
                if (buffer.Count < 3) return false;
                byte byteCount = buffer[2];
                bool validByteCount = respFn is 3 or 4
                    ? byteCount is >= 2 and <= 250 && byteCount % 2 == 0
                    : byteCount is >= 1 and <= 250;
                if (!validByteCount)
                {
                    buffer.RemoveAt(0);
                    continue;
                }
                expectedLen = 5 + byteCount;
            }
            else if (respFn is 5 or 6 or 15 or 16)
            {
                expectedLen = 8;
            }
            else
            {
                expectedLen = 0;
                for (int len = 4; len <= Math.Min(buffer.Count, 256); len++)
                {
                    var probe = buffer.GetRange(0, len).ToArray();
                    if (Crc16.Verify(probe))
                    {
                        expectedLen = len;
                        break;
                    }
                }
                if (expectedLen == 0)
                {
                    if (buffer.Count >= 8)
                    {
                        buffer.RemoveAt(0);
                        continue;
                    }
                    return false;
                }
            }

            if (buffer.Count < expectedLen)
                return false;

            var candidate = buffer.GetRange(0, expectedLen).ToArray();
            if (!Crc16.Verify(candidate))
            {
                buffer.RemoveAt(0);
                continue;
            }

            buffer.RemoveRange(0, expectedLen);
            if (expectedUnitId.HasValue && candidate[0] != expectedUnitId.Value)
                continue;
            if (expectedFn.HasValue && respFn != expectedFn.Value && respFn != (byte)(expectedFn.Value | 0x80))
                continue;

            matchedFrame = candidate;
            return true;
        }

        return false;
    }

    private void OnChannelStateChanged(object? sender, ChannelState state)
    {
        if (state is ChannelState.Disconnected or ChannelState.Faulted or ChannelState.Reconnecting)
        {
            CancelAllPendingTcpTransactions($"通讯状态切换为 {state}");
            lock (_tcpAccumulatorLock)
                _tcpRxAccumulator.Clear();
        }
        StateChanged?.Invoke(this, state);
    }

    private void OnChannelDataReceived(object? sender, ChannelDataReceivedEventArgs e)
    {
        if (_activeChannel is TcpClientChannel && !_pendingTcpTransactions.IsEmpty)
            DispatchPendingTcpFrames(e.Bytes);

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

    private void OnChannelErrorOccurred(object? sender, string message)
    {
        AppLogger.Warn($"通讯通道提示: {message}");
        ErrorOccurred?.Invoke(this, message);
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _ = DisconnectAsync();
        _serialExchangeLock.Dispose();
        _sendLock.Dispose();
    }
}
