using System;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;

namespace Yuandl.ModbusAssistant.Models.Raw;

/// <summary>
/// 无协议调试报文条目实体
/// 保留原始收发字节数组，杜绝数据失真
/// </summary>
public class RawMessageItem
{
    public DateTime Timestamp { get; }
    public bool IsTx { get; }
    public DataEncoding Encoding { get; }
    public byte[] RawBytes { get; }
    public string FormattedText { get; }
    public int Length => RawBytes.Length;

    public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss.fff");
    public string DirectionText => IsTx ? "TX 发送" : "RX 接收";

    public RawMessageItem(bool isTx, byte[] rawBytes, DataEncoding encoding, DateTime? timestamp = null)
    {
        IsTx = isTx;
        RawBytes = rawBytes ?? Array.Empty<byte>();
        Encoding = encoding;
        Timestamp = timestamp ?? DateTime.Now;
        FormattedText = ByteUtils.DecodeReceived(RawBytes, encoding);
    }
}
