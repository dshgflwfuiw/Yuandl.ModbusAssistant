using System;
using System.Buffers.Binary;
using System.Text;
using Yuandl.ModbusAssistant.Core.Enums;

namespace Yuandl.ModbusAssistant.Core.Utils;

/// <summary>
/// 字节数组与文本格式编解码核心工具类
/// 提供高容错的 HEX 转换、ASCII/UTF-8 编解码与结束符追加能力
/// </summary>
public static class ByteUtils
{
    private static readonly char[] HexLookup = "0123456789ABCDEF".ToCharArray();

    /// <summary>
    /// 将 HEX 字符串安全转换为原始字节数组
    /// 支持常见的格式容错：忽略空格、逗号、短横线、制表符、换行符及 '0x'/'0X' 前缀
    /// </summary>
    /// <param name="hexString">输入的 HEX 字符串</param>
    /// <returns>解析后的原始字节数组</returns>
    /// <exception cref="ArgumentNullException">输入为 null</exception>
    /// <exception cref="FormatException">字符包含非十六进制字符或长度不满足整字节</exception>
    public static byte[] HexToBytes(string hexString)
    {
        if (hexString == null)
        {
            throw new ArgumentNullException(nameof(hexString));
        }

        // 过滤常见的视觉分隔符与前缀，保留纯十六进制有效字符
        var cleanChars = new char[hexString.Length];
        int cleanLen = 0;

        for (int i = 0; i < hexString.Length; i++)
        {
            char c = hexString[i];
            if (char.IsWhiteSpace(c) || c == ',' || c == '-' || c == ';' || c == ':')
            {
                continue;
            }

            // 处理 '0x' 或 '0X' 前缀
            if (c == '0' && i + 1 < hexString.Length && (hexString[i + 1] == 'x' || hexString[i + 1] == 'X'))
            {
                i++; // 跳过 'x'
                continue;
            }

            cleanChars[cleanLen++] = c;
        }

        if (cleanLen == 0)
        {
            return Array.Empty<byte>();
        }

        // 工业协议要求：每个字节由两位十六进制组成，奇数位必属不完整输入
        if ((cleanLen % 2) != 0)
        {
            throw new FormatException($"HEX 字符串字符数必须为偶数 (当前有效字符数: {cleanLen})，请检查是否遗漏了高位或低位字符。");
        }

        int byteCount = cleanLen / 2;
        byte[] result = new byte[byteCount];

        for (int i = 0; i < byteCount; i++)
        {
            int high = ParseHexNibble(cleanChars[i * 2]);
            int low = ParseHexNibble(cleanChars[i * 2 + 1]);

            if (high == -1 || low == -1)
            {
                char invalidChar = high == -1 ? cleanChars[i * 2] : cleanChars[i * 2 + 1];
                throw new FormatException($"检测到非法的十六进制字符 '{invalidChar}'，有效字符范围为 [0-9, a-f, A-F]。");
            }

            result[i] = (byte)((high << 4) | low);
        }

        return result;
    }

    /// <summary>
    /// 将字节数组格式化为标准大写十六进制字符串（如 "01 03 00 00 00 0A C5 CD"）
    /// </summary>
    public static string BytesToHex(ReadOnlySpan<byte> bytes, string separator = " ")
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        separator ??= string.Empty;
        var sb = new StringBuilder(bytes.Length * (2 + separator.Length));

        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            sb.Append(HexLookup[b >> 4]);
            sb.Append(HexLookup[b & 0x0F]);

            if (separator.Length > 0 && i < bytes.Length - 1)
            {
                sb.Append(separator);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 自动追加指定的报文结束符
    /// </summary>
    public static byte[] AppendDelimiter(byte[] data, EndingDelimiter delimiter)
    {
        data ??= Array.Empty<byte>();

        return delimiter switch
        {
            EndingDelimiter.CR => Combine(data, stackalloc byte[] { 0x0D }),
            EndingDelimiter.LF => Combine(data, stackalloc byte[] { 0x0A }),
            EndingDelimiter.CRLF => Combine(data, stackalloc byte[] { 0x0D, 0x0A }),
            _ => data
        };
    }

    /// <summary>
    /// 根据编码格式将字符串解析为字节数组，并选择性追加结束符
    /// </summary>
    public static byte[] EncodeInput(string text, DataEncoding encoding, EndingDelimiter delimiter = EndingDelimiter.None)
    {
        if (string.IsNullOrEmpty(text))
        {
            return AppendDelimiter(Array.Empty<byte>(), delimiter);
        }

        byte[] rawBytes = encoding switch
        {
            DataEncoding.Hex => HexToBytes(text),
            DataEncoding.Ascii => Encoding.ASCII.GetBytes(text),
            DataEncoding.Utf8 => Encoding.UTF8.GetBytes(text),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), $"不支持的数据编码类型: {encoding}")
        };

        return AppendDelimiter(rawBytes, delimiter);
    }

    /// <summary>
    /// 根据编码格式将接收到的字节流解码为可读文本
    /// </summary>
    public static string DecodeReceived(ReadOnlySpan<byte> bytes, DataEncoding encoding)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        return encoding switch
        {
            DataEncoding.Hex => BytesToHex(bytes),
            DataEncoding.Ascii => Encoding.ASCII.GetString(bytes),
            DataEncoding.Utf8 => Encoding.UTF8.GetString(bytes),
            _ => BytesToHex(bytes)
        };
    }

    private static int ParseHexNibble(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => -1
        };
    }

    private static byte[] Combine(byte[] first, ReadOnlySpan<byte> second)
    {
        byte[] combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        second.CopyTo(combined.AsSpan(first.Length));
        return combined;
    }
}
