using System.IO.Ports;
using Yuandl.ModbusAssistant.Core.Enums;

namespace Yuandl.ModbusAssistant.Models;

public sealed class SerialPortParameters
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = true;

    public SerialPortParameters Clone() => (SerialPortParameters)MemberwiseClone();
}

public sealed class TcpClientParameters
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5020;
    public int ConnectTimeoutMs { get; set; } = 3000;
    public bool AutoReconnect { get; set; } = false;
    public int ReconnectIntervalMs { get; set; } = 3000;
}

public sealed class UdpParameters
{
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 502;
    public int LocalPort { get; set; } = 0;
}

public sealed class TcpServerParameters
{
    public int ListenPort { get; set; } = 6000;
}

public sealed class TcpServerClientSession
{
    public string SessionId { get; set; } = string.Empty;
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.Now;
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

    public string ConnectedAtText => ConnectedAt.ToString("HH:mm:ss");
    public string DisplayLabel => string.IsNullOrEmpty(SessionId)
        ? RemoteAddress
        : $"{SessionId} · {RemoteAddress}:{RemotePort}";
}

public sealed class ServerRegisterDefinition
{
    public ServerRegisterArea Area { get; set; } = ServerRegisterArea.HoldingRegister;
    public int Address { get; set; }
    public ServerRegisterDataType DataType { get; set; } = ServerRegisterDataType.UInt16;
    public ServerRegisterByteOrder ByteOrder { get; set; } = ServerRegisterByteOrder.BigEndian;
    public string InitialValue { get; set; } = "0";
    public string Note { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = "0";
}

public sealed class ModbusServerConfiguration
{
    public int Version { get; set; } = 1;
    public int Port { get; set; } = 5020;
    public int Station { get; set; } = 1;
    public ModbusServerTransportMode ServerMode { get; set; } = ModbusServerTransportMode.Tcp;
    public SerialPortParameters ServerSerial { get; set; } = new();
    public List<ServerRegisterDefinition> Registers { get; set; } = new();
}

public sealed class ChannelStatistics
{
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public long PacketsSent { get; set; }
    public long PacketsReceived { get; set; }
    public long ErrorsCount { get; set; }
}

public sealed class DeviceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "Modbus TCP";
    public string Endpoint { get; set; } = "127.0.0.1:5020";
    public int StationId { get; set; } = 1;
}

public sealed class AppSettings
{
    public bool IsDarkTheme { get; set; } = false;
    public int DefaultTimeoutMs { get; set; } = 1000;
    public int DefaultRetryTimes { get; set; } = 2;
}

public sealed class RawMessageItem
{
    public string Timestamp { get; set; } = string.Empty;
    public string Direction { get; set; } = "RX";
    public string Content { get; set; } = string.Empty;
    public byte[] RawBytes { get; set; } = Array.Empty<byte>();
    public int ByteLength => RawBytes.Length;
    public string? SessionLabel { get; set; }
}

public sealed class TrafficEventArgs : EventArgs
{
    public required string Direction { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Hex { get; init; }
    public required DateTime Timestamp { get; init; }
    public CommunicationType? ChannelType { get; init; }
    public string? SessionId { get; init; }
    public string? SessionLabel { get; init; }
}

public sealed class AnalyzedPacketEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public string Direction { get; set; } = "RX";
    public ModbusTransport Transport { get; set; }
    public string Hex { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public double? RttMs { get; set; }
    public string? SessionLabel { get; set; }
    public string RttText => RttMs.HasValue ? $"{RttMs.Value:0.#} ms" : "—";
}

public sealed class RegisterMonitorRow
{
    public string ChannelId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Address { get; set; }
    public string RawValue { get; set; } = string.Empty;
    public double Value { get; set; }
    public bool IsDigital { get; set; }
    public bool IsEnabledInScope { get; set; } = true;
    public string TraceColorHex { get; set; } = "#22D3EE";

    private string _note = string.Empty;
    public string Note
    {
        get => _note;
        set
        {
            if (_note != value)
            {
                _note = value ?? string.Empty;
                OnNoteChanged?.Invoke(ChannelId, _note);
            }
        }
    }

    public Action<string, string>? OnNoteChanged { get; set; }
    public string DisplayValue => IsDigital ? (Value >= 0.5 ? "ON (1)" : "OFF (0)") : Value.ToString("G10");
}

public sealed class ScopeChannelConfig
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#22D3EE";
    public bool Enabled { get; set; } = true;
    public bool IsDigital { get; set; }
    public string LatestText { get; set; } = "—";
    public string RangeText { get; set; } = string.Empty;
}

public sealed class ScopeSample
{
    public DateTime Timestamp { get; init; }
    public Dictionary<string, double> Values { get; init; } = new();
}
