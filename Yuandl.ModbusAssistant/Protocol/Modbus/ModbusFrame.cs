using System.Text;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

public sealed class ModbusResponse
{
    public ushort? TransactionId { get; init; }
    public byte UnitId { get; init; }
    public byte FunctionCode { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
    public bool IsException { get; init; }
    public byte? ExceptionCode { get; init; }
    public ushort[] Registers { get; init; } = Array.Empty<ushort>();
}

public sealed class ModbusParsedRequest
{
    public ushort? TransactionId { get; init; }
    public byte UnitId { get; init; }
    public byte FunctionCode { get; init; }
    public ushort Address { get; init; }
    public ushort? Quantity { get; init; }
    public ushort[] Values { get; init; } = Array.Empty<ushort>();
}

public static class ModbusFrame
{
    private static ushort[] ExtractRegisters(ReadOnlySpan<byte> payload)
    {
        var data = payload.Length > 0 && payload[0] == payload.Length - 1
            ? payload[1..]
            : payload;

        if (data.Length % 2 != 0)
            return Array.Empty<ushort>();

        var result = new ushort[data.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);

        return result;
    }

    public static bool[] ExtractBits(ReadOnlySpan<byte> payload, int quantity)
    {
        var data = payload.Length > 0 && payload[0] == payload.Length - 1
            ? payload[1..]
            : payload;

        int maxBits = data.Length * 8;
        int count = Math.Min(Math.Max(0, quantity), maxBits);
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
            bits[i] = (data[i / 8] & (1 << (i % 8))) != 0;

        return bits;
    }

    private static (byte[] Pdu, ushort? TransactionId) UnwrapTransport(
        ReadOnlySpan<byte> frame,
        ModbusTransport transport)
    {
        if (transport == ModbusTransport.Rtu)
        {
            if (!Crc16.Verify(frame))
                throw new InvalidDataException("Modbus RTU CRC 校验失败");
            return (frame[..^2].ToArray(), null);
        }

        if (transport == ModbusTransport.Tcp)
        {
            if (frame.Length < 8)
                throw new InvalidDataException("Modbus TCP 帧长度不足");

            ushort transactionId = (ushort)((frame[0] << 8) | frame[1]);
            ushort protocolId = (ushort)((frame[2] << 8) | frame[3]);
            if (protocolId != 0)
                throw new InvalidDataException("Modbus TCP 协议标识不匹配");

            ushort len = (ushort)((frame[4] << 8) | frame[5]);
            if (len != frame.Length - 6)
                throw new InvalidDataException("Modbus TCP 长度字段不匹配");

            return (frame[6..].ToArray(), transactionId);
        }

        string text = Encoding.ASCII.GetString(frame);
        if (!text.StartsWith(':') || !text.EndsWith("\r\n", StringComparison.Ordinal))
            throw new InvalidDataException("Modbus ASCII 帧格式错误");

        byte[] hex = ByteUtils.HexToBytes(text[1..^2]);
        if (!LrcUtils.Verify(hex))
            throw new InvalidDataException("Modbus ASCII LRC 校验失败");

        return (hex[..^1], null);
    }

    public static byte[] BuildRequest(
        byte unitId,
        byte functionCode,
        ushort address,
        ushort quantity,
        ModbusTransport transport = ModbusTransport.Rtu,
        ushort transactionId = 1)
    {
        if (quantity == 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "数量必须在 1 至 65535 之间");

        var pdu = new byte[6];
        pdu[0] = unitId;
        pdu[1] = functionCode;
        pdu[2] = (byte)(address >> 8);
        pdu[3] = (byte)(address & 0xFF);
        pdu[4] = (byte)(quantity >> 8);
        pdu[5] = (byte)(quantity & 0xFF);
        return Encode(pdu, transport, transactionId);
    }

    public static byte[] BuildWriteSingle(
        byte unitId,
        byte functionCode,
        ushort address,
        ushort value,
        ModbusTransport transport = ModbusTransport.Rtu,
        ushort transactionId = 1)
    {
        var pdu = new byte[6];
        pdu[0] = unitId;
        pdu[1] = functionCode;
        pdu[2] = (byte)(address >> 8);
        pdu[3] = (byte)(address & 0xFF);
        pdu[4] = (byte)(value >> 8);
        pdu[5] = (byte)(value & 0xFF);
        return Encode(pdu, transport, transactionId);
    }

    public static byte[] BuildWriteMultiple(
        byte unitId,
        byte functionCode,
        ushort address,
        IReadOnlyList<ushort> values,
        ModbusTransport transport = ModbusTransport.Rtu,
        ushort transactionId = 1)
    {
        if (functionCode == 15)
        {
            if (values.Count == 0 || values.Count > 1968)
                throw new ArgumentOutOfRangeException(nameof(values), "写入线圈数量必须在 1 至 1968 之间");

            int quantity = values.Count;
            int byteCount = (quantity + 7) / 8;
            var pdu = new byte[7 + byteCount];
            pdu[0] = unitId;
            pdu[1] = functionCode;
            pdu[2] = (byte)(address >> 8);
            pdu[3] = (byte)(address & 0xFF);
            pdu[4] = (byte)(quantity >> 8);
            pdu[5] = (byte)(quantity & 0xFF);
            pdu[6] = (byte)byteCount;

            for (int i = 0; i < quantity; i++)
            {
                if (values[i] != 0)
                    pdu[7 + (i / 8)] |= (byte)(1 << (i % 8));
            }
            return Encode(pdu, transport, transactionId);
        }

        if (values.Count == 0 || values.Count > 123)
            throw new ArgumentOutOfRangeException(nameof(values), "写入寄存器数量必须在 1 至 123 之间");

        var regPdu = new byte[7 + values.Count * 2];
        regPdu[0] = unitId;
        regPdu[1] = functionCode;
        regPdu[2] = (byte)(address >> 8);
        regPdu[3] = (byte)(address & 0xFF);
        regPdu[4] = (byte)(values.Count >> 8);
        regPdu[5] = (byte)(values.Count & 0xFF);
        regPdu[6] = (byte)(values.Count * 2);

        for (int i = 0; i < values.Count; i++)
        {
            regPdu[7 + i * 2] = (byte)(values[i] >> 8);
            regPdu[8 + i * 2] = (byte)(values[i] & 0xFF);
        }
        return Encode(regPdu, transport, transactionId);
    }

    public static ushort[] ParseMultiValues(string text, bool isCoils)
    {
        var tokens = text
            .Split(new[] { ' ', '\t', ',', ';', '，', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            throw new FormatException("请至少输入一个数值");

        if (isCoils)
        {
            if (tokens.Length > 1968)
                throw new FormatException("批量写入线圈最多支持 1968 个");

            var result = new ushort[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                var lower = tokens[i].Trim().ToLowerInvariant();
                if (lower is "1" or "true" or "on") result[i] = 1;
                else if (lower is "0" or "false" or "off") result[i] = 0;
                else throw new FormatException($"无效的线圈值 \"{tokens[i]}\"，请使用 1/0 或 true/false");
            }
            return result;
        }

        if (tokens.Length > 123)
            throw new FormatException("批量写入寄存器最多支持 123 个");

        var regs = new ushort[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i].Trim();
            if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ushort.TryParse(tok[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort hexVal))
                    throw new FormatException($"无效的寄存器值 \"{tok}\"，范围为 0~65535 或 0x0000~0xFFFF");
                regs[i] = hexVal;
            }
            else if (!ushort.TryParse(tok, out ushort decVal))
            {
                throw new FormatException($"无效的寄存器值 \"{tok}\"，范围为 0~65535 或 0x0000~0xFFFF");
            }
            else
            {
                regs[i] = decVal;
            }
        }
        return regs;
    }

    public static byte[] Encode(
        ReadOnlySpan<byte> rtuPdu,
        ModbusTransport transport,
        ushort transactionId = 1)
    {
        if (transport == ModbusTransport.Rtu)
        {
            var result = new byte[rtuPdu.Length + 2];
            rtuPdu.CopyTo(result);
            ushort crc = Crc16.Compute(rtuPdu);
            result[^2] = (byte)(crc & 0xFF);
            result[^1] = (byte)(crc >> 8);
            return result;
        }

        if (transport == ModbusTransport.Tcp)
        {
            var result = new byte[rtuPdu.Length + 6];
            result[0] = (byte)(transactionId >> 8);
            result[1] = (byte)(transactionId & 0xFF);
            result[2] = 0;
            result[3] = 0;
            result[4] = (byte)(rtuPdu.Length >> 8);
            result[5] = (byte)(rtuPdu.Length & 0xFF);
            rtuPdu.CopyTo(result.AsSpan(6));
            return result;
        }

        string ascii = ByteUtils.BytesToHex(rtuPdu, string.Empty);
        byte lrc = LrcUtils.Compute(rtuPdu);
        string frameStr = $":{ascii}{lrc:X2}\r\n";
        return Encoding.ASCII.GetBytes(frameStr);
    }

    public static ModbusParsedRequest ParseRequest(ReadOnlySpan<byte> frame, ModbusTransport transport)
    {
        var (pdu, transactionId) = UnwrapTransport(frame, transport);
        if (pdu.Length < 6)
            throw new InvalidDataException("Modbus 请求长度不足");

        byte unitId = pdu[0];
        byte functionCode = pdu[1];
        ushort address = (ushort)((pdu[2] << 8) | pdu[3]);

        if (functionCode is 5 or 6)
        {
            ushort val = (ushort)((pdu[4] << 8) | pdu[5]);
            return new ModbusParsedRequest
            {
                TransactionId = transactionId,
                UnitId = unitId,
                FunctionCode = functionCode,
                Address = address,
                Quantity = 1,
                Values = new[] { val }
            };
        }

        ushort quantity = (ushort)((pdu[4] << 8) | pdu[5]);
        if (functionCode is 15 or 16)
        {
            if (pdu.Length < 7)
                throw new InvalidDataException("Modbus 多写请求长度不足");

            byte byteCount = pdu[6];
            if (pdu.Length != 7 + byteCount)
                throw new InvalidDataException("Modbus 多写请求字节数不匹配");

            var values = new List<ushort>();
            if (functionCode == 15)
            {
                for (int i = 0; i < quantity; i++)
                {
                    bool bit = (pdu[7 + (i / 8)] & (1 << (i % 8))) != 0;
                    values.Add((ushort)(bit ? 1 : 0));
                }
            }
            else
            {
                for (int i = 0; i < quantity && 7 + i * 2 + 1 < pdu.Length; i++)
                    values.Add((ushort)((pdu[7 + i * 2] << 8) | pdu[8 + i * 2]));
            }

            return new ModbusParsedRequest
            {
                TransactionId = transactionId,
                UnitId = unitId,
                FunctionCode = functionCode,
                Address = address,
                Quantity = quantity,
                Values = values.ToArray()
            };
        }

        return new ModbusParsedRequest
        {
            TransactionId = transactionId,
            UnitId = unitId,
            FunctionCode = functionCode,
            Address = address,
            Quantity = quantity
        };
    }

    public static ModbusResponse ParseResponse(ReadOnlySpan<byte> frame, ModbusTransport transport)
    {
        var (pdu, transactionId) = UnwrapTransport(frame, transport);
        if (pdu.Length < 2)
            throw new InvalidDataException("Modbus 响应长度不足");

        bool isException = (pdu[1] & 0x80) != 0;
        if (isException)
        {
            return new ModbusResponse
            {
                TransactionId = transactionId,
                UnitId = pdu[0],
                FunctionCode = pdu[1],
                IsException = true,
                ExceptionCode = pdu.Length > 2 ? pdu[2] : null
            };
        }

        var payload = pdu[2..];
        return new ModbusResponse
        {
            TransactionId = transactionId,
            UnitId = pdu[0],
            FunctionCode = pdu[1],
            Payload = payload,
            IsException = false,
            ExceptionCode = null,
            Registers = ExtractRegisters(payload)
        };
    }
}
