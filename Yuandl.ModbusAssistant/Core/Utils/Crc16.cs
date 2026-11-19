namespace Yuandl.ModbusAssistant.Core.Utils;

public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x0001) != 0)
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                else
                    crc >>= 1;
            }
        }
        return crc;
    }

    public static bool Verify(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        var expected = Compute(frame[..^2]);
        var actual = (ushort)(frame[^2] | (frame[^1] << 8));
        return expected == actual;
    }
}
