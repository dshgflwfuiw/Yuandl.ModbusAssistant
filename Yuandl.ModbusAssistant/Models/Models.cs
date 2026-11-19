using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.CompilerServices;
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

public sealed class TcpServerParameters : INotifyPropertyChanged
{
    private int _listenPort = 6000;

    public int ListenPort
    {
        get => _listenPort;
        set
        {
            if (_listenPort == value) return;
            _listenPort = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListenPort)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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

public sealed class MonitorConfiguration
{
    public string? SelectedGroupId { get; set; }
    public List<MonitorPollingGroupConfiguration> Groups { get; set; } = new();
    public Dictionary<string, string> ChannelNotes { get; set; } = new();
}

public sealed class MonitorPollingGroupConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ModbusTransport Transport { get; set; } = ModbusTransport.Tcp;
    public byte UnitId { get; set; } = 1;
    public byte FunctionCode { get; set; } = 3;
    public ushort StartAddress { get; set; }
    public ushort Quantity { get; set; } = 6;
    public RegisterDataType DataType { get; set; } = RegisterDataType.UInt16;
    public RegisterByteOrder ByteOrder { get; set; } = RegisterByteOrder.BigEndian;
    public double Scale { get; set; } = 1.0;
    public int IntervalMs { get; set; } = 500;
    public bool IsEnabled { get; set; } = true;
    public List<MonitorPollingDataConfiguration> DataItems { get; set; } = new();
}

public sealed class MonitorPollingDataConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public byte FunctionCode { get; set; }
    public ushort StartAddress { get; set; }
    public ushort Quantity { get; set; }
    public RegisterDataType DataType { get; set; }
    public RegisterByteOrder ByteOrder { get; set; }
    public double Scale { get; set; }
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

public sealed class RegisterMonitorRow : INotifyPropertyChanged
{
    private string _channelId = string.Empty;
    private string _label = string.Empty;
    private int _address;
    private string _rawValue = string.Empty;
    private double _value;
    private bool _isDigital;
    private bool _isEnabledInScope = true;
    private string _traceColorHex = "#22D3EE";
    private string _note = string.Empty;

    public string ChannelId { get => _channelId; set => SetProperty(ref _channelId, value); }
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public int Address { get => _address; set => SetProperty(ref _address, value); }
    public string RawValue { get => _rawValue; set => SetProperty(ref _rawValue, value); }
    public double Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
                OnPropertyChanged(nameof(DisplayValue));
        }
    }
    public bool IsDigital
    {
        get => _isDigital;
        set
        {
            if (SetProperty(ref _isDigital, value))
                OnPropertyChanged(nameof(DisplayValue));
        }
    }
    public bool IsEnabledInScope
    {
        get => _isEnabledInScope;
        set
        {
            if (SetProperty(ref _isEnabledInScope, value))
                OnPropertyChanged(nameof(ScopeToggleText));
        }
    }
    public string ScopeToggleText => IsEnabledInScope ? "显示" : "隐藏";
    public string TraceColorHex { get => _traceColorHex; set => SetProperty(ref _traceColorHex, value); }

    public string Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value ?? string.Empty))
            {
                OnNoteChanged?.Invoke(ChannelId, _note);
            }
        }
    }

    public Action<string, string>? OnNoteChanged { get; set; }
    public string DisplayValue => IsDigital ? (Value >= 0.5 ? "ON (1)" : "OFF (0)") : Value.ToString("G10");

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ScopeChannelConfig : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _label = string.Empty;
    private string _colorHex = "#22D3EE";
    private bool _enabled = true;
    private bool _isDigital;
    private string _latestText = "—";
    private string _rangeText = string.Empty;

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string ColorHex { get => _colorHex; set => SetProperty(ref _colorHex, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool IsDigital { get => _isDigital; set => SetProperty(ref _isDigital, value); }
    public string LatestText { get => _latestText; set => SetProperty(ref _latestText, value); }
    public string RangeText { get => _rangeText; set => SetProperty(ref _rangeText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class ScopeSample
{
    public DateTime Timestamp { get; init; }
    public Dictionary<string, double> Values { get; init; } = new();
}
