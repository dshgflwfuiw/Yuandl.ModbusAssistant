namespace Yuandl.ModbusAssistant.Core.Enums;

/// <summary>
/// 通讯物理介质/协议类型枚举
/// </summary>
public enum CommunicationType
{
    /// <summary>
    /// RS232 / RS485 串行通讯端口
    /// </summary>
    SerialPort,

    /// <summary>
    /// TCP 客户端（基于 Socket 异步通讯）
    /// </summary>
    TcpClient,

    /// <summary>
    /// UDP 数据报通讯
    /// </summary>
    Udp
}
