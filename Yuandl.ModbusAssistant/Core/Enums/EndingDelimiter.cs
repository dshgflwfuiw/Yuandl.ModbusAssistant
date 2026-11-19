namespace Yuandl.ModbusAssistant.Core.Enums;

/// <summary>
/// 报文尾部追加结束符枚举
/// </summary>
public enum EndingDelimiter
{
    /// <summary>
    /// 不追加任何结束符
    /// </summary>
    None,

    /// <summary>
    /// 回车符 CR (\r, 0x0D)
    /// </summary>
    CR,

    /// <summary>
    /// 换行符 LF (\n, 0x0A)
    /// </summary>
    LF,

    /// <summary>
    /// 回车换行符 CRLF (\r\n, 0x0D 0x0A)
    /// </summary>
    CRLF
}
