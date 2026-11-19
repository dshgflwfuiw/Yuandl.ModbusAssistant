namespace Yuandl.ModbusAssistant.Core.Utils;

public static class LrcUtils
{
    public static byte Compute(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        for (int i = 0; i < data.Length; i++)
            sum += data[i];
        return (byte)((-sum) & 0xFF);
    }

    public static bool Verify(ReadOnlySpan<byte> frameWithoutColonsAndCrLf)
    {
        if (frameWithoutColonsAndCrLf.Length < 2) return false;
        var expected = Compute(frameWithoutColonsAndCrLf[..^1]);
        return expected == frameWithoutColonsAndCrLf[^1];
    }
}
