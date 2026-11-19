using System;

namespace Yuandl.ModbusAssistant.Core.Utils;

/// <summary>
/// Modbus ASCII 纵向冗余校验 (LRC, Longitudinal Redundancy Check)
/// 算法：对报文中冒号之后、回车之前的所有字节累加取模，并取二进制补码
/// </summary>
public static class LrcUtils
{
    public static byte Compute(ReadOnlySpan<byte> buffer)
    {
        byte sum = 0;
        foreach (byte b in buffer)
        {
            sum += b;
        }
        return (byte)(-sum);
    }

    public static bool Verify(ReadOnlySpan<byte> bufferWithLrc)
    {
        if (bufferWithLrc.IsEmpty)
        {
            return false;
        }

        byte sum = 0;
        foreach (byte b in bufferWithLrc)
        {
            sum += b;
        }

        return sum == 0;
    }
}
