namespace Yuandl.ModbusAssistant.Models.Connection;

/// <summary>
/// UDP 通讯配置参数模型
/// </summary>
public class UdpParameters
{
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 502;
    public int LocalPort { get; set; } = 0; // 0 表示由系统动态分配本地端口

    public override string ToString() => $"UDP ({RemoteHost}:{RemotePort})";
}
