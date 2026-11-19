using System;
using System.Threading;
using System.Threading.Tasks;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Communication.Abstractions;

/// <summary>
/// 通道抽象基类
/// 封装通用状态机、并发发送锁 (SemaphoreSlim)、流量统计及事件安全触发逻辑
/// </summary>
public abstract class ChannelBase : IChannel
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ChannelState _state = ChannelState.Disconnected;
    private bool _isDisposed;

    public abstract CommunicationType Type { get; }
    public abstract string ChannelInfo { get; }
    public ChannelStatistics Statistics { get; } = new();

    public ChannelState State
    {
        get => _state;
        protected set
        {
            if (_state != value)
            {
                var old = _state;
                _state = value;
                OnStateChanged(new ChannelStateChangedEventArgs(old, value));
            }
        }
    }

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public abstract Task<bool> OpenAsync(CancellationToken cancellationToken = default);

    public abstract Task CloseAsync();

    /// <summary>
    /// 线程安全的并发发送实现
    /// </summary>
    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (State != ChannelState.Connected)
        {
            throw new InvalidOperationException($"当前通道未处于连接就绪状态 (当前状态: {State})");
        }

        if (data.IsEmpty)
        {
            return 0;
        }

        // 使用信号量保障并发写入的物理线缆互斥性，避免多线程发送导致报文碎片交替穿插
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int sent = await SendInternalAsync(data, cancellationToken).ConfigureAwait(false);
            Statistics.AddBytesSent(sent);
            return sent;
        }
        catch (Exception ex)
        {
            Statistics.IncrementErrors();
            OnError(ex);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 由子类实现的底层发送逻辑
    /// </summary>
    protected abstract Task<int> SendInternalAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    protected void OnDataReceived(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty) return;
        Statistics.AddBytesReceived(data.Length);
        try
        {
            DataReceived?.Invoke(this, new DataReceivedEventArgs(data));
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    protected void OnStateChanged(ChannelStateChangedEventArgs args)
    {
        try
        {
            StateChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    protected void OnError(Exception ex)
    {
        try
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        catch
        {
            // 防止外部异常处理失败引起次生崩溃
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _sendLock.Dispose();
            }
            _isDisposed = true;
        }
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // 忽略关闭时的异常
        }
    }
}
