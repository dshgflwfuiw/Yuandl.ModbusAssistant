namespace Yuandl.ModbusAssistant.Core.Enums;

/// <summary>
/// 通讯通道运行状态枚举
/// </summary>
public enum ChannelState
{
    /// <summary>
    /// 未连接 / 已断开
    /// </summary>
    Disconnected,

    /// <summary>
    /// 正在建立连接
    /// </summary>
    Connecting,

    /// <summary>
    /// 已连接并处于就绪状态
    /// </summary>
    Connected,

    /// <summary>
    /// 发生异常正在自动重连
    /// </summary>
    Reconnecting,

    /// <summary>
    /// 通道发生致命错误故障
    /// </summary>
    Faulted
}
