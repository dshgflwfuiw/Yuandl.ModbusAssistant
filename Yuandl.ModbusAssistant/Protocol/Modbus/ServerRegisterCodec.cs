using System.Buffers.Binary;
using System.Globalization;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

public static class ServerRegisterCodec
{
    public static int GetWidth(ServerRegisterDataType type) => type switch
    {
        ServerRegisterDataType.Bit or ServerRegisterDataType.Int16 or ServerRegisterDataType.UInt16 => 1,
        ServerRegisterDataType.Int32 or ServerRegisterDataType.UInt32 or ServerRegisterDataType.Float => 2,
        ServerRegisterDataType.Double => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type), $"未知数据类型: {type}")
    };

    public static ushort[] EncodeRegisters(
        string value,
        ServerRegisterDataType type,
        ServerRegisterByteOrder byteOrder)
    {
        int width = GetWidth(type);
        var bytes = new byte[width * 2];

        switch (type)
        {
            case ServerRegisterDataType.Int16:
                if (!short.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out short i16))
                    throw new FormatException($"Int16 值 \"{value}\" 无效 [-32768, 32767]");
                BinaryPrimitives.WriteInt16BigEndian(bytes, i16);
                break;

            case ServerRegisterDataType.UInt16:
                if (!ushort.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort u16))
                    throw new FormatException($"UInt16 值 \"{value}\" 无效 [0, 65535]");
                BinaryPrimitives.WriteUInt16BigEndian(bytes, u16);
                break;

            case ServerRegisterDataType.Int32:
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32))
                    throw new FormatException($"Int32 值 \"{value}\" 无效");
                BinaryPrimitives.WriteInt32BigEndian(bytes, i32);
                break;

            case ServerRegisterDataType.UInt32:
                if (!uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u32))
                    throw new FormatException($"UInt32 值 \"{value}\" 无效");
                BinaryPrimitives.WriteUInt32BigEndian(bytes, u32);
                break;

            case ServerRegisterDataType.Float:
                if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f32) || !float.IsFinite(f32))
                    throw new FormatException($"Float 值 \"{value}\" 无效");
                BinaryPrimitives.WriteSingleBigEndian(bytes, f32);
                break;

            case ServerRegisterDataType.Double:
                if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double f64) || !double.IsFinite(f64))
                    throw new FormatException($"Double 值 \"{value}\" 无效");
                BinaryPrimitives.WriteDoubleBigEndian(bytes, f64);
                break;

            default:
                throw new InvalidOperationException($"数据类型 {type} 不是寄存器数值类型");
        }

        ApplyByteOrder(bytes, byteOrder);

        var registers = new ushort[width];
        for (int i = 0; i < width; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i * 2, 2));

        return registers;
    }

    public static string DecodeRegisters(
        IReadOnlyList<ushort> registers,
        ServerRegisterDataType type,
        ServerRegisterByteOrder byteOrder)
    {
        int width = GetWidth(type);
        var bytes = new byte[width * 2];
        for (int i = 0; i < width; i++)
        {
            ushort reg = i < registers.Count ? registers[i] : (ushort)0;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), reg);
        }

        ApplyByteOrder(bytes, byteOrder);

        return type switch
        {
            ServerRegisterDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ServerRegisterDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ServerRegisterDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ServerRegisterDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ServerRegisterDataType.Float => Math.Round(BinaryPrimitives.ReadSingleBigEndian(bytes), 6).ToString(CultureInfo.InvariantCulture),
            ServerRegisterDataType.Double => Math.Round(BinaryPrimitives.ReadDoubleBigEndian(bytes), 10).ToString(CultureInfo.InvariantCulture),
            _ => (registers.Count > 0 ? registers[0] : 0).ToString(CultureInfo.InvariantCulture)
        };
    }

    public static bool EncodeBit(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "true" or "1") return true;
        if (normalized is "false" or "0") return false;
        throw new FormatException("线圈/离散输入的值必须为 true/false 或 1/0");
    }

    private static void ApplyByteOrder(byte[] bytes, ServerRegisterByteOrder byteOrder)
    {
        switch (byteOrder)
        {
            case ServerRegisterByteOrder.LittleEndian:
                Array.Reverse(bytes);
                break;
            case ServerRegisterByteOrder.WordSwap:
                for (int i = 0; i + 3 < bytes.Length; i += 4)
                {
                    (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
                    (bytes[i + 1], bytes[i + 3]) = (bytes[i + 3], bytes[i + 1]);
                }
                break;
            case ServerRegisterByteOrder.ByteSwap:
                for (int i = 0; i + 1 < bytes.Length; i += 2)
                    (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
                break;
        }
    }

    public static void Validate(ServerRegisterDefinition definition)
    {
        if (definition.Address is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(definition), "起始地址必须在 0 至 65535 之间");

        bool bitArea = definition.Area is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput;
        if (bitArea != (definition.DataType == ServerRegisterDataType.Bit))
            throw new InvalidOperationException("线圈和离散输入必须使用 Bit 类型；寄存器区必须使用数值类型");

        int width = GetWidth(definition.DataType);
        if (definition.Address + width > 65536)
            throw new ArgumentOutOfRangeException(nameof(definition), "寄存器定义超出 Modbus 地址范围");

        if (bitArea)
            _ = EncodeBit(definition.InitialValue);
        else
            _ = EncodeRegisters(definition.InitialValue, definition.DataType, definition.ByteOrder);
    }

    public static void ValidateNoOverlap(IEnumerable<ServerRegisterDefinition> definitions)
    {
        foreach (var group in definitions.GroupBy(d => d.Area))
        {
            var ordered = group.OrderBy(d => d.Address).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                int prevEnd = prev.Address + GetWidth(prev.DataType);
                if (ordered[i].Address < prevEnd)
                    throw new InvalidOperationException($"{group.Key} 地址 {ordered[i].Address} 与地址 {prev.Address} 的定义重叠");
            }
        }
    }
}
