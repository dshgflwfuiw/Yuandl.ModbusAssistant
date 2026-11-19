using System.IO.Ports;

namespace Yuandl.ModbusAssistant.Models.Connection;

/// <summary>
/// 串口通讯配置参数模型
/// </summary>
public class SerialPortParameters
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.None;
    public Handshake Handshake { get; set; } = Handshake.None;
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = true;
    public int ReadTimeoutMs { get; set; } = 1000;
    public int WriteTimeoutMs { get; set; } = 1000;

    public override string ToString() => $"{PortName} ({BaudRate},{DataBits},{Parity},{StopBits})";
}
