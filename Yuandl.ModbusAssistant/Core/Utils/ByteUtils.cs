using System.Text;
using Yuandl.ModbusAssistant.Core.Enums;

namespace Yuandl.ModbusAssistant.Core.Utils;

public static class ByteUtils
{
    public static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Array.Empty<byte>();

        var cleaned = hex.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "")
            .Replace("-", "")
            .Replace(",", "")
            .Replace(";", "")
            .Replace("\r", "")
            .Replace("\n", "");

        if (cleaned.Length % 2 != 0)
            throw new FormatException("十六进制字符串长度必须为偶数");

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    public static string BytesToHex(ReadOnlySpan<byte> bytes, string separator = " ")
    {
        if (bytes.IsEmpty) return string.Empty;
        var sb = new StringBuilder(bytes.Length * (2 + separator.Length));
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0 && separator.Length > 0)
                sb.Append(separator);
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public static byte[] EncodeText(string text, DataEncoding encoding, EndingDelimiter delimiter)
    {
        var suffix = delimiter switch
        {
            EndingDelimiter.CR => "\r",
            EndingDelimiter.LF => "\n",
            EndingDelimiter.CRLF => "\r\n",
            _ => string.Empty
        };

        return encoding switch
        {
            DataEncoding.Hex => Combine(HexToBytes(text), Encoding.ASCII.GetBytes(suffix)),
            DataEncoding.Ascii => Encoding.ASCII.GetBytes(text + suffix),
            DataEncoding.Utf8 => Encoding.UTF8.GetBytes(text + suffix),
            _ => Encoding.UTF8.GetBytes(text + suffix)
        };
    }

    public static string DecodeBytes(ReadOnlySpan<byte> bytes, DataEncoding encoding)
    {
        return encoding switch
        {
            DataEncoding.Hex => BytesToHex(bytes),
            DataEncoding.Ascii => Encoding.ASCII.GetString(bytes),
            DataEncoding.Utf8 => Encoding.UTF8.GetString(bytes),
            _ => BytesToHex(bytes)
        };
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        if (second.Length == 0) return first;
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
}
