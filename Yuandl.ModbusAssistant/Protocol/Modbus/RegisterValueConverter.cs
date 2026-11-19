using System.Buffers.Binary;
using Yuandl.ModbusAssistant.Core.Enums;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

public static class RegisterValueConverter
{
    public static int GetWidth(RegisterDataType type) => type switch
    {
        RegisterDataType.Int32 or RegisterDataType.UInt32 or RegisterDataType.Float => 2,
        RegisterDataType.Double => 4,
        _ => 1
    };

    public static double Convert(
        IReadOnlyList<ushort> registers,
        RegisterDataType type,
        RegisterByteOrder order = RegisterByteOrder.BigEndian,
        double scale = 1.0)
    {
        if (registers.Count == 0)
            throw new ArgumentException("至少需要一个寄存器", nameof(registers));

        int width = GetWidth(type);
        if (registers.Count < width)
            throw new ArgumentException($"{type} 至少需要 {width} 个寄存器", nameof(registers));

        var bytes = new byte[registers.Count * 2];
        for (int i = 0; i < registers.Count; i++)
        {
            bytes[i * 2] = (byte)(registers[i] >> 8);
            bytes[i * 2 + 1] = (byte)(registers[i] & 0xFF);
        }

        if (order == RegisterByteOrder.LittleEndian)
        {
            for (int i = 0; i + 1 < bytes.Length; i += 2)
                (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        }

        double rawValue = type switch
        {
            RegisterDataType.Bool => BinaryPrimitives.ReadUInt16BigEndian(bytes) != 0 ? 1.0 : 0.0,
            RegisterDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes),
            RegisterDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
            RegisterDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            RegisterDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            RegisterDataType.Float => BinaryPrimitives.ReadSingleBigEndian(bytes),
            RegisterDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(bytes),
            _ => throw new NotSupportedException($"不支持的寄存器类型: {type}")
        };

        double scaled = rawValue * scale;
        return double.IsFinite(scaled) ? Math.Round(scaled, 10) : scaled;
    }
}
