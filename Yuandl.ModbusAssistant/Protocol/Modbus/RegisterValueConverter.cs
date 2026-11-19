using System.Buffers.Binary;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

public enum RegisterDataType { Int16, UInt16, Int32, UInt32, Float, Double }
public enum RegisterByteOrder { BigEndian, LittleEndian }

public static class RegisterValueConverter
{
    public static object Convert(ReadOnlySpan<ushort> registers, RegisterDataType type, RegisterByteOrder order = RegisterByteOrder.BigEndian, double scale = 1)
    {
        if (registers.IsEmpty) throw new ArgumentException("至少需要一个寄存器", nameof(registers));
        var bytes = new byte[registers.Length * 2]; for (var i = 0; i < registers.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), registers[i]);
        if (order == RegisterByteOrder.LittleEndian) for (var i = 0; i + 1 < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        object value = type switch
        {
            RegisterDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes), RegisterDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
            RegisterDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes), RegisterDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            RegisterDataType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)), RegisterDataType.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes)), _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        return value is double d ? d * scale : System.Convert.ToDouble(value) * scale;
    }
}
