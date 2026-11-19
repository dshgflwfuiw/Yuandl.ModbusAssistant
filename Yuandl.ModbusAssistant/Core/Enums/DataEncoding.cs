namespace Yuandl.ModbusAssistant.Core.Enums;

/// <summary>
/// 原始数据编码格式枚举
/// </summary>
public enum DataEncoding
{
    /// <summary>
    /// 十六进制（如：01 03 00 00 00 0A）
    /// </summary>
    Hex,

    /// <summary>
    /// ASCII 文本
    /// </summary>
    Ascii,

    /// <summary>
    /// UTF-8 文本
    /// </summary>
    Utf8
}
