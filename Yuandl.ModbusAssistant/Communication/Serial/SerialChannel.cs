using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Communication.Serial;

/// <summary>
/// 串口通讯通道 (RS232 / RS485)
/// 基于 System.IO.Ports.SerialPort 实现全异步收发与硬件拔出异常侦测
/// </summary>
public class SerialChannel : ChannelBase
{
    private readonly SerialPortParameters _parameters;
    private SerialPort? _serialPort;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;

    public override CommunicationType Type => CommunicationType.SerialPort;
    public override string ChannelInfo => _parameters.ToString();

    public SerialChannel(SerialPortParameters parameters)
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
            await Task.Run(() =>
            {
                _serialPort = new SerialPort
                {
                    PortName = _parameters.PortName,
                    BaudRate = _parameters.BaudRate,
                    DataBits = _parameters.DataBits,
                    Parity = _parameters.Parity,
                    StopBits = _parameters.StopBits,
                    Handshake = _parameters.Handshake,
                    DtrEnable = _parameters.DtrEnable,
                    RtsEnable = _parameters.RtsEnable,
                    ReadTimeout = _parameters.ReadTimeoutMs,
                    WriteTimeout = _parameters.WriteTimeoutMs
                };

                _serialPort.Open();
                // 清空缓冲区残余脏数据
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
            }, cancellationToken).ConfigureAwait(false);

            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);

            State = ChannelState.Connected;
            return true;
        }
        catch (Exception ex)
        {
            State = ChannelState.Faulted;
            OnError(ex);
            await CloseAsync().ConfigureAwait(false);
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
                // 等待读取任务平稳退出
                await Task.WhenAny(_readTask, Task.Delay(500)).ConfigureAwait(false);
            }
        }
        catch
        {
            // 忽略取消等待时的异常
        }
        finally
        {
            _readCts?.Dispose();
            _readCts = null;
            _readTask = null;
        }

        try
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
            }
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
        finally
        {
            _serialPort?.Dispose();
            _serialPort = null;
            State = ChannelState.Disconnected;
        }
    }

    protected override async Task<int> SendInternalAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_serialPort == null || !_serialPort.IsOpen)
        {
            throw new InvalidOperationException("串口未打开，无法发送数据");
        }

        var stream = _serialPort.BaseStream;
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return data.Length;
    }

    /// <summary>
    /// 后台独立连续读取循环
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        Stream? stream = null;

        try
        {
            stream = _serialPort?.BaseStream;
        }
        catch (Exception ex)
        {
            OnError(ex);
            State = ChannelState.Faulted;
            return;
        }

        while (!cancellationToken.IsCancellationRequested && stream != null)
        {
            try
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    OnDataReceived(new ReadOnlyMemory<byte>(buffer, 0, bytesRead));
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
                    // 硬件串口拔出、线路中断等物理异常侦测
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
            try
            {
                _readCts?.Cancel();
                _serialPort?.Dispose();
            }
            catch
            {
            }
        }
        base.Dispose(disposing);
    }
}
