using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using Yuandl.ModbusAssistant.Core.Enums;

namespace Yuandl.ModbusAssistant.Models;

public sealed class SerialPortParameters : INotifyPropertyChanged
{
    private string _portName = "COM1";
    private int _baudRate = 9600;
    private int _dataBits = 8;
    private Parity _parity = Parity.None;
    private StopBits _stopBits = StopBits.One;
    private bool _dtrEnable = true;
    private bool _rtsEnable = true;

    public string PortName { get => _portName; set => SetProperty(ref _portName, value); }
    public int BaudRate { get => _baudRate; set => SetProperty(ref _baudRate, value); }
    public int DataBits { get => _dataBits; set => SetProperty(ref _dataBits, value); }
    public Parity Parity { get => _parity; set => SetProperty(ref _parity, value); }
    public StopBits StopBits { get => _stopBits; set => SetProperty(ref _stopBits, value); }
    public bool DtrEnable { get => _dtrEnable; set => SetProperty(ref _dtrEnable, value); }
    public bool RtsEnable { get => _rtsEnable; set => SetProperty(ref _rtsEnable, value); }

    public SerialPortParameters Clone() => new()
    {
        PortName = PortName,
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
        DtrEnable = DtrEnable,
        RtsEnable = RtsEnable
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class TcpClientParameters : INotifyPropertyChanged
{
    private string _host = "127.0.0.1";
    private int _port = 5020;
    private int _connectTimeoutMs = 3000;
    private bool _autoReconnect;
    private int _reconnectIntervalMs = 3000;

    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public int ConnectTimeoutMs { get => _connectTimeoutMs; set => SetProperty(ref _connectTimeoutMs, value); }
    public bool AutoReconnect { get => _autoReconnect; set => SetProperty(ref _autoReconnect, value); }
    public int ReconnectIntervalMs { get => _reconnectIntervalMs; set => SetProperty(ref _reconnectIntervalMs, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class UdpParameters : INotifyPropertyChanged
{
    private string _remoteHost = "127.0.0.1";
    private int _remotePort = 502;
    private int _localPort;

    public string RemoteHost { get => _remoteHost; set => SetProperty(ref _remoteHost, value); }
    public int RemotePort { get => _remotePort; set => SetProperty(ref _remotePort, value); }
    public int LocalPort { get => _localPort; set => SetProperty(ref _localPort, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class TcpServerParameters : INotifyPropertyChanged
{
    private string _bindAddress = "127.0.0.1";
    private int _listenPort = 6000;
    private int _maxClients = 32;
    private int _idleTimeoutMs = 60000;
    private int _maxFrameLength = 4096;
    private int _sendQueueLimit = 64;
    private string _allowedClientIps = string.Empty;

    public string BindAddress
    {
        get => _bindAddress;
        set => SetProperty(ref _bindAddress, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim());
    }

    public int ListenPort
    {
        get => _listenPort;
        set => SetProperty(ref _listenPort, Math.Clamp(value, 1, 65535));
    }

    public int MaxClients
    {
        get => _maxClients;
        set => SetProperty(ref _maxClients, Math.Clamp(value, 1, 256));
    }

    public int IdleTimeoutMs
    {
        get => _idleTimeoutMs;
        set => SetProperty(ref _idleTimeoutMs, Math.Clamp(value, 0, 3600000));
    }

    public int MaxFrameLength
    {
        get => _maxFrameLength;
        set => SetProperty(ref _maxFrameLength, Math.Clamp(value, 64, 65536));
    }

    public int SendQueueLimit
    {
        get => _sendQueueLimit;
        set => SetProperty(ref _sendQueueLimit, Math.Clamp(value, 1, 1024));
    }

    public string AllowedClientIps
    {
        get => _allowedClientIps;
        set => SetProperty(ref _allowedClientIps, value ?? string.Empty);
    }

    public TcpServerParameters Clone() => new()
    {
        BindAddress = BindAddress,
        ListenPort = ListenPort,
        MaxClients = MaxClients,
        IdleTimeoutMs = IdleTimeoutMs,
        MaxFrameLength = MaxFrameLength,
        SendQueueLimit = SendQueueLimit,
        AllowedClientIps = AllowedClientIps
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
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

public sealed class ModbusServerSecurityOptions
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int MaxClients { get; set; } = 32;
    public int IdleTimeoutMs { get; set; } = 60000;
    public int MaxFrameLength { get; set; } = 260;
    public int SendQueueLimit { get; set; } = 64;
    public string AllowedClientIps { get; set; } = string.Empty;

    public ModbusServerSecurityOptions Clone() => new()
    {
        BindAddress = BindAddress,
        MaxClients = MaxClients,
        IdleTimeoutMs = IdleTimeoutMs,
        MaxFrameLength = MaxFrameLength,
        SendQueueLimit = SendQueueLimit,
        AllowedClientIps = AllowedClientIps
    };
}

public sealed class ModbusServerConfiguration
{
    public int Version { get; set; } = 2;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5020;
    public int Station { get; set; } = 1;
    public int MaxClients { get; set; } = 32;
    public int IdleTimeoutMs { get; set; } = 60000;
    public int MaxFrameLength { get; set; } = 260;
    public int SendQueueLimit { get; set; } = 64;
    public string AllowedClientIps { get; set; } = string.Empty;
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

public sealed class WorkspaceConfiguration
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public AppSettings Settings { get; set; } = new();
    public List<DeviceProfile> Profiles { get; set; } = new();
    public ModbusServerConfiguration ServerConfig { get; set; } = new();
    public MonitorConfiguration MonitorConfig { get; set; } = new();
}

public sealed class PortableWorkspaceFile
{
    public string Format { get; set; } = "yuandl-modbus-workspace";
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<DeviceProfile> Profiles { get; set; } = new();
    public ModbusServerConfiguration ServerConfig { get; set; } = new();
    public MonitorConfiguration MonitorConfig { get; set; } = new();
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
