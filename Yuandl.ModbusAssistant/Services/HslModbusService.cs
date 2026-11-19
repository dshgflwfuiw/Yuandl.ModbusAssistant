using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Services;

public sealed class HslModbusService : IAsyncDisposable
{
    private readonly ICommunicationService _communicationService;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private TaskCompletionSource<byte[]>? _response;

    public bool IsOpen => _communicationService.IsConnected;

    public HslModbusService(ICommunicationService communicationService)
    {
        _communicationService = communicationService;
        _communicationService.DataReceived += OnDataReceived;
    }

    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        if (!_communicationService.IsConnected)
        {
            throw new InvalidOperationException("通讯链路未就绪或已断开");
        }

        await _exchangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _response = response;
            await _communicationService.SendAsync(request, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var registration = timeout.Token.Register(() => response.TrySetCanceled(timeout.Token));
            try
            {
                return await response.Task.ConfigureAwait(false);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
                if (ReferenceEquals(_response, response))
                {
                    _response = null;
                }
            }
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort count, byte station = 1, CancellationToken cancellationToken = default)
    {
        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var transport = _communicationService.CurrentType switch
        {
            CommunicationType.SerialPort => ModbusTransport.Rtu,
            CommunicationType.TcpClient => ModbusTransport.Tcp,
            CommunicationType.Udp => ModbusTransport.Rtu,
            _ => throw new InvalidOperationException("通讯链路尚未打开")
        };

        var transactionId = (ushort)Environment.TickCount;
        var request = ModbusFrame.BuildRequest(station, 3, address, count, transport, transactionId);
        var responseBytes = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        var response = ModbusFrame.ParseResponse(responseBytes, transport);
        if (response.IsException)
        {
            throw new InvalidOperationException($"Modbus 异常码 0x{response.ExceptionCode:X2}");
        }

        return response.Registers;
    }

    private void OnDataReceived(object? sender, DataReceivedEventArgs e)
    {
        _response?.TrySetResult(e.Data.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _communicationService.DataReceived -= OnDataReceived;
        _exchangeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
