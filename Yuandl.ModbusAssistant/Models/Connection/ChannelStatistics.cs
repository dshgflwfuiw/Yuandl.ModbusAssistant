using System;
using System.Threading;

namespace Yuandl.ModbusAssistant.Models.Connection;

/// <summary>
/// 通讯流量与健康度统计模型
/// 内部字段采用 Interlocked 进行原子操作，保证高并发收发下的线程安全性
/// </summary>
public class ChannelStatistics
{
    private long _bytesSent;
    private long _bytesReceived;
    private long _framesSent;
    private long _framesReceived;
    private long _errorsCount;
    private DateTime _lastActivityTime = DateTime.Now;

    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long ErrorsCount => Interlocked.Read(ref _errorsCount);
    public DateTime LastActivityTime => _lastActivityTime;

    public void AddBytesSent(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _bytesSent, count);
            Interlocked.Increment(ref _framesSent);
            _lastActivityTime = DateTime.Now;
        }
    }

    public void AddBytesReceived(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _bytesReceived, count);
            Interlocked.Increment(ref _framesReceived);
            _lastActivityTime = DateTime.Now;
        }
    }

    public void IncrementErrors()
    {
        Interlocked.Increment(ref _errorsCount);
        _lastActivityTime = DateTime.Now;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _framesSent, 0);
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _errorsCount, 0);
        _lastActivityTime = DateTime.Now;
    }
}
