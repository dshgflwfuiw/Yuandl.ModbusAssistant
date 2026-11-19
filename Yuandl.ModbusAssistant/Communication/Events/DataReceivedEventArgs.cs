using System;

namespace Yuandl.ModbusAssistant.Communication.Events;

/// <summary>
/// 通道接收到原始数据时的事件参数
/// </summary>
public class DataReceivedEventArgs : EventArgs
{
    public ReadOnlyMemory<byte> Data { get; }
    public DateTime Timestamp { get; }

    public DataReceivedEventArgs(ReadOnlyMemory<byte> data, DateTime? timestamp = null)
    {
        Data = data;
        Timestamp = timestamp ?? DateTime.Now;
    }
}
