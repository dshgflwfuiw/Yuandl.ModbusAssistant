namespace Yuandl.ModbusAssistant.Models.Connection;

/// <summary>
/// TCP 客户端通讯配置参数模型
/// </summary>
public class TcpClientParameters
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ReadTimeoutMs { get; set; } = 2000;
    public int WriteTimeoutMs { get; set; } = 2000;
    public bool AutoReconnect { get; set; } = false;
    public int ReconnectIntervalMs { get; set; } = 3000;

    public override string ToString() => $"TCP Client ({Host}:{Port})";
}
