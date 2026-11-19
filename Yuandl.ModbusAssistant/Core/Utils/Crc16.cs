using System;

namespace Yuandl.ModbusAssistant.Core.Utils;

/// <summary>
/// Modbus 标准 CRC16 校验算法实现
/// 多项式：0xA001 (反向多项式，对应标准 X16 + X15 + X2 + 1)
/// 初始值：0xFFFF
/// 结果：低字节在前 (LSB First)，高字节在后 (MSB Last)
/// </summary>
public static class Crc16
{
    private static readonly ushort[] CrcTable = new ushort[256];

    static Crc16()
    {
        // 预计算 256 项查表数组，提升工业现场每秒万次校验的高性能与确定性耗时
        for (ushort i = 0; i < 256; i++)
        {
            ushort value = i;
            for (int j = 0; j < 8; j++)
            {
                if ((value & 1) != 0)
                {
                    value = (ushort)((value >> 1) ^ 0xA001);
                }
                else
                {
                    value >>= 1;
                }
            }
            CrcTable[i] = value;
        }
    }

    /// <summary>
    /// 计算指定字节缓冲区的 CRC16 校验码
    /// </summary>
    /// <param name="buffer">待计算的数据切片</param>
    /// <returns>16位校验码（例如 0xC5CD，低字节为 0xC5，高字节为 0xCD）</returns>
    public static ushort Compute(ReadOnlySpan<byte> buffer)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in buffer)
        {
            byte index = (byte)(crc ^ b);
            crc = (ushort)((crc >> 8) ^ CrcTable[index]);
        }
        return crc;
    }

    /// <summary>
    /// 验证包含末尾 2 字节 CRC 的数据流是否合法
    /// </summary>
    /// <param name="bufferWithCrc">包含末尾 CRC16（低字节在前）的完整报文</param>
    /// <returns>校验通过返回 true，否则返回 false</returns>
    public static bool Verify(ReadOnlySpan<byte> bufferWithCrc)
    {
        if (bufferWithCrc.Length < 3)
        {
            return false;
        }

        int payloadLen = bufferWithCrc.Length - 2;
        ushort calculated = Compute(bufferWithCrc[..payloadLen]);

        byte expectedLow = (byte)(calculated & 0xFF);
        byte expectedHigh = (byte)((calculated >> 8) & 0xFF);

        return bufferWithCrc[payloadLen] == expectedLow && bufferWithCrc[payloadLen + 1] == expectedHigh;
    }
}
