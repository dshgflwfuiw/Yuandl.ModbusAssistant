using System.Buffers.Binary;
using System.Globalization;
using Yuandl.ModbusAssistant.Models.Connection;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

public static class ServerRegisterCodec
{
    public static ushort[] EncodeRegisters(string value, ServerRegisterDataType type, ServerRegisterByteOrder byteOrder)
    {
        var width = GetWidth(type);
        var bytes = new byte[width * 2];
        switch (type)
        {
            case ServerRegisterDataType.Int16:
                BinaryPrimitives.WriteInt16BigEndian(bytes, short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                break;
            case ServerRegisterDataType.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(bytes, ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                break;
            case ServerRegisterDataType.Int32:
                BinaryPrimitives.WriteInt32BigEndian(bytes, int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                break;
            case ServerRegisterDataType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(bytes, uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                break;
            case ServerRegisterDataType.Float:
                BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)));
                break;
            case ServerRegisterDataType.Double:
                BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)));
                break;
            default:
                throw new ArgumentException($"数据类型 {type} 不是寄存器数值类型", nameof(type));
        }

        if (byteOrder == ServerRegisterByteOrder.LittleEndian)
        {
            Array.Reverse(bytes);
        }

        var registers = new ushort[width];
        for (var index = 0; index < width; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index * 2, 2));
        }

        return registers;
    }

    public static bool EncodeBit(string value)
    {
        if (!bool.TryParse(value, out var bit))
        {
            throw new FormatException("线圈/离散输入的值必须为 true 或 false");
        }

        return bit;
    }

    public static int GetWidth(ServerRegisterDataType type) => type switch
    {
        ServerRegisterDataType.Bit or ServerRegisterDataType.Int16 or ServerRegisterDataType.UInt16 => 1,
        ServerRegisterDataType.Int32 or ServerRegisterDataType.UInt32 or ServerRegisterDataType.Float => 2,
        ServerRegisterDataType.Double => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static void Validate(ServerRegisterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(definition.Area)) throw new ArgumentOutOfRangeException(nameof(definition), "未知寄存器区域");
        if (!Enum.IsDefined(definition.DataType)) throw new ArgumentOutOfRangeException(nameof(definition), "未知数据类型");
        if (!Enum.IsDefined(definition.ByteOrder)) throw new ArgumentOutOfRangeException(nameof(definition), "未知字节序");
        var bitArea = definition.Area is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput;
        if (bitArea != (definition.DataType == ServerRegisterDataType.Bit))
        {
            throw new ArgumentException("线圈和离散输入必须使用 Bit 类型；寄存器区必须使用数值类型", nameof(definition));
        }

        if (definition.Address + definition.Width > ushort.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "寄存器定义超出 Modbus 地址范围");
        }

        if (bitArea)
        {
            _ = EncodeBit(definition.InitialValue);
        }
        else
        {
            _ = EncodeRegisters(definition.InitialValue, definition.DataType, definition.ByteOrder);
        }
    }

    public static void ValidateNoOverlap(IEnumerable<ServerRegisterDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var group in definitions.GroupBy(definition => definition.Area))
        {
            var ordered = group.OrderBy(definition => definition.Address).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previousEnd = ordered[index - 1].Address + ordered[index - 1].Width;
                if (ordered[index].Address < previousEnd)
                {
                    throw new ArgumentException($"{group.Key} 地址 {ordered[index].Address} 与地址 {ordered[index - 1].Address} 的定义重叠");
                }
            }
        }
    }
}
