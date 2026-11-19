using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Yuandl.ModbusAssistant.Models.Connection;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Services;

public sealed class HslModbusServerService : IDisposable
{
    private readonly object _sync = new();
    private readonly ushort[] _holdingRegisters = new ushort[ushort.MaxValue + 1];
    private readonly ushort[] _inputRegisters = new ushort[ushort.MaxValue + 1];
    private readonly bool[] _coils = new bool[ushort.MaxValue + 1];
    private readonly bool[] _discreteInputs = new bool[ushort.MaxValue + 1];
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private IReadOnlyList<ServerRegisterDefinition> _registers = Array.Empty<ServerRegisterDefinition>();
    private TcpListener? _listener;
    private CancellationTokenSource? _stopCts;
    private Task? _acceptTask;
    private byte _station;

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }

    public void ConfigureRegisters(IEnumerable<ServerRegisterDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var validated = definitions.ToArray();
        foreach (var definition in validated) ServerRegisterCodec.Validate(definition);
        ServerRegisterCodec.ValidateNoOverlap(validated);

        lock (_sync)
        {
            var previous = _registers;
            foreach (var removedOrChanged in previous.Where(oldDefinition => !validated.Contains(oldDefinition)))
            {
                SetDefinitionValue(removedOrChanged, clear: true);
            }

            _registers = validated;
            foreach (var addedOrChanged in validated.Where(newDefinition => !previous.Contains(newDefinition)))
            {
                SetDefinitionValue(addedOrChanged, clear: false);
            }
        }
    }

    public void Start(int port, byte station = 1)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (station == 0) throw new ArgumentOutOfRangeException(nameof(station));

        Stop();
        lock (_sync)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _station = station;
            _listener = listener;
            _stopCts = new CancellationTokenSource();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptTask = AcceptClientsAsync(listener, _stopCts.Token);
        }
    }

    public void Stop()
    {
        Task[] tasks;
        lock (_sync)
        {
            tasks = _clients.Values.Append(_acceptTask).Where(task => task is not null).Cast<Task>().ToArray();
            StopCore();
        }

        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }

    private Task? StopCore()
    {
        var acceptTask = _acceptTask;
        _stopCts?.Cancel();
        _listener?.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        _clients.Clear();
        _listener = null;
        _acceptTask = null;
        _stopCts?.Dispose();
        _stopCts = null;
        Port = 0;
        return acceptTask;
    }

    private async Task AcceptClientsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var task = HandleClientAsync(client, cancellationToken);
            _clients[client] = task;
            _ = task.ContinueWith(completedTask => _clients.TryRemove(client, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) break;
                    if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) != 0) break;
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                    if (length is < 2 or > 254) break;
                    var request = new byte[length - 1];
                    if (!await ReadExactlyAsync(stream, request, cancellationToken).ConfigureAwait(false)) break;

                    var responsePdu = ProcessRequest(header[6], request);
                    var response = new byte[7 + responsePdu.Length];
                    header.AsSpan(0, 4).CopyTo(response);
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)(responsePdu.Length + 1));
                    response[6] = header[6];
                    responsePdu.CopyTo(response, 7);
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private byte[] ProcessRequest(byte unitId, byte[] request)
    {
        if (request.Length < 1) return [0x80, 3];
        var function = request[0];
        if (unitId != _station) return [ (byte)(function | 0x80), 0x0B ];

        try
        {
            return function switch
            {
                1 => ReadBits(function, request, _coils),
                2 => ReadBits(function, request, _discreteInputs),
                3 => ReadRegisters(function, request, _holdingRegisters),
                4 => ReadRegisters(function, request, _inputRegisters),
                5 => WriteSingleCoil(function, request),
                6 => WriteSingleRegister(function, request),
                15 => WriteMultipleCoils(function, request),
                16 => WriteMultipleRegisters(function, request),
                _ => [(byte)(function | 0x80), 1]
            };
        }
        catch (ArgumentException)
        {
            return [(byte)(function | 0x80), 2];
        }
        catch (IndexOutOfRangeException)
        {
            return [(byte)(function | 0x80), 3];
        }
    }

    private byte[] ReadBits(byte function, byte[] request, bool[] source)
    {
        if (request.Length != 5) throw new IndexOutOfRangeException();
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
        if (quantity is 0 or > 2000 || address + quantity > source.Length) throw new ArgumentException();
        var bytes = new byte[(quantity + 7) / 8];
        lock (_sync)
        {
            for (var index = 0; index < quantity; index++)
            {
                if (source[address + index]) bytes[index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return [function, (byte)bytes.Length, .. bytes];
    }

    private byte[] ReadRegisters(byte function, byte[] request, ushort[] source)
    {
        if (request.Length != 5) throw new IndexOutOfRangeException();
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
        if (quantity is 0 or > 125 || address + quantity > source.Length) throw new ArgumentException();
        var response = new byte[2 + quantity * 2];
        response[0] = function;
        response[1] = (byte)(quantity * 2);
        lock (_sync)
        {
            for (var index = 0; index < quantity; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + index * 2, 2), source[address + index]);
            }
        }

        return response;
    }

    private byte[] WriteSingleCoil(byte function, byte[] request)
    {
        if (request.Length != 5) throw new IndexOutOfRangeException();
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
        var value = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
        if (value is not (0x0000 or 0xFF00)) throw new IndexOutOfRangeException();
        lock (_sync)
        {
            EnsureWritable(ServerRegisterArea.Coil, address);
            _coils[address] = value == 0xFF00;
        }
        return request;
    }

    private byte[] WriteSingleRegister(byte function, byte[] request)
    {
        if (request.Length != 5) throw new IndexOutOfRangeException();
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
        lock (_sync)
        {
            EnsureWritable(ServerRegisterArea.HoldingRegister, address);
            _holdingRegisters[address] = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
        }
        return request;
    }

    private byte[] WriteMultipleCoils(byte function, byte[] request)
    {
        if (request.Length < 6) throw new IndexOutOfRangeException();
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
        var byteCount = request[5];
        if (quantity is 0 or > 1968 || byteCount != (quantity + 7) / 8 || request.Length != 6 + byteCount || address + quantity > _coils.Length) throw new IndexOutOfRangeException();
        lock (_sync)
        {
            for (var index = 0; index < quantity; index++) EnsureWritable(ServerRegisterArea.Coil, (ushort)(address + index));
            for (var index = 0; index < quantity; index++) _coils[address + index] = (request[6 + index / 8] & (1 << (index % 8))) != 0;
        }

        return [function, request[1], request[2], request[3], request[4]];
    }

    private byte[] WriteMultipleRegisters(byte function, byte[] request)
    {
        if (request.Length < 6) throw new IndexOutOfRangeException();
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
        var byteCount = request[5];
        if (quantity is 0 or > 123 || byteCount != quantity * 2 || request.Length != 6 + byteCount || address + quantity > _holdingRegisters.Length) throw new IndexOutOfRangeException();
        lock (_sync)
        {
            for (var index = 0; index < quantity; index++) EnsureWritable(ServerRegisterArea.HoldingRegister, (ushort)(address + index));
            for (var index = 0; index < quantity; index++) _holdingRegisters[address + index] = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6 + index * 2, 2));
        }

        return [function, request[1], request[2], request[3], request[4]];
    }

    private void EnsureWritable(ServerRegisterArea area, ushort address)
    {
        var definition = _registers.FirstOrDefault(item => item.Area == area && address >= item.Address && address < item.Address + item.Width);
        if (definition is null) throw new ArgumentException("写入地址未定义");
    }

    private void SetDefinitionValue(ServerRegisterDefinition definition, bool clear)
    {
        if (definition.Area is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput)
        {
            var target = definition.Area == ServerRegisterArea.Coil ? _coils : _discreteInputs;
            var value = !clear && ServerRegisterCodec.EncodeBit(definition.InitialValue);
            for (var index = 0; index < definition.Width; index++) target[definition.Address + index] = value;
            return;
        }

        var targetRegisters = definition.Area == ServerRegisterArea.HoldingRegister ? _holdingRegisters : _inputRegisters;
        var encoded = clear ? new ushort[definition.Width] : ServerRegisterCodec.EncodeRegisters(definition.InitialValue, definition.DataType, definition.ByteOrder);
        Array.Copy(encoded, 0, targetRegisters, definition.Address, encoded.Length);
    }

    public void Dispose() => Stop();
}
