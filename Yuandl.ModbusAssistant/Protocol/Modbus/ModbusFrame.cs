using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;
using Yuandl.ModbusAssistant.Core.Utils;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

public enum ModbusTransport { Rtu, Ascii, Tcp }

public sealed record ModbusResponse(byte UnitId, byte FunctionCode, byte[] Payload, bool IsException = false, byte? ExceptionCode = null)
{
    public ushort[] Registers
    {
        get
        {
            var data = Payload.Length > 0 && Payload[0] == Payload.Length - 1 ? Payload.AsSpan(1) : Payload.AsSpan();
            if (data.Length % 2 != 0) return Array.Empty<ushort>();
            var result = new ushort[data.Length / 2];
            for (var i = 0; i < result.Length; i++) result[i] = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i * 2, 2));
            return result;
        }
    }
}

public static class ModbusFrame
{
    public static byte[] BuildRequest(byte unitId, byte functionCode, ushort address, ushort quantity, ModbusTransport transport = ModbusTransport.Rtu, ushort transactionId = 1)
    {
        if (quantity == 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var pdu = new byte[6]; pdu[0] = unitId; pdu[1] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), address); BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), quantity);
        return Encode(pdu, transport, transactionId);
    }

    public static byte[] BuildWriteSingle(byte unitId, byte functionCode, ushort address, ushort value, ModbusTransport transport = ModbusTransport.Rtu, ushort transactionId = 1)
    {
        var pdu = new byte[6]; pdu[0] = unitId; pdu[1] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), address); BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), value);
        return Encode(pdu, transport, transactionId);
    }

    public static byte[] BuildWriteMultiple(byte unitId, byte functionCode, ushort address, ReadOnlySpan<ushort> values, ModbusTransport transport = ModbusTransport.Rtu, ushort transactionId = 1)
    {
        if (values.IsEmpty || values.Length > 123) throw new ArgumentOutOfRangeException(nameof(values));
        var pdu = new byte[7 + values.Length * 2]; pdu[0] = unitId; pdu[1] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), address); BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), (ushort)values.Length);
        pdu[6] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(7 + i * 2), values[i]);
        return Encode(pdu, transport, transactionId);
    }

    public static byte[] Encode(ReadOnlySpan<byte> rtuPdu, ModbusTransport transport, ushort transactionId = 1)
    {
        if (transport == ModbusTransport.Rtu) { var result = new byte[rtuPdu.Length + 2]; rtuPdu.CopyTo(result); var crc = Crc16.Compute(rtuPdu); result[^2] = (byte)crc; result[^1] = (byte)(crc >> 8); return result; }
        if (transport == ModbusTransport.Tcp) { var result = new byte[rtuPdu.Length + 6]; BinaryPrimitives.WriteUInt16BigEndian(result, transactionId); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), 0); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)rtuPdu.Length); rtuPdu.CopyTo(result.AsSpan(6)); return result; }
        var ascii = Convert.ToHexString(rtuPdu.ToArray()); var lrc = LrcUtils.Compute(rtuPdu); return Encoding.ASCII.GetBytes($":{ascii}{lrc:X2}\r\n");
    }

    public static ModbusResponse ParseResponse(ReadOnlySpan<byte> frame, ModbusTransport transport)
    {
        byte[] pdu;
        if (transport == ModbusTransport.Rtu) { if (!Crc16.Verify(frame)) throw new FormatException("Modbus RTU CRC 校验失败"); pdu = frame[..^2].ToArray(); }
        else if (transport == ModbusTransport.Tcp)
        {
            if (frame.Length < 8) throw new FormatException("Modbus TCP 帧长度不足");
            if (BinaryPrimitives.ReadUInt16BigEndian(frame[2..]) != 0) throw new FormatException("Modbus TCP 协议标识不匹配");
            var len = BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);
            if (len != frame.Length - 6) throw new FormatException("Modbus TCP 长度字段不匹配");
            pdu = frame[6..].ToArray();
        }
        else { var text = Encoding.ASCII.GetString(frame); if (!text.StartsWith(':') || !text.EndsWith("\r\n", StringComparison.Ordinal)) throw new FormatException("Modbus ASCII 帧格式错误"); var hex = Convert.FromHexString(text[1..^2]); if (!LrcUtils.Verify(hex)) throw new FormatException("Modbus ASCII LRC 校验失败"); pdu = hex[..^1]; }
        if (pdu.Length < 2) throw new FormatException("Modbus 响应长度不足");
        var exception = (pdu[1] & 0x80) != 0;
        return exception ? new ModbusResponse(pdu[0], pdu[1], Array.Empty<byte>(), true, pdu.Length > 2 ? pdu[2] : null) : new ModbusResponse(pdu[0], pdu[1], pdu[2..].ToArray());
    }
}
