using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class ServerRegisterRowViewModel : ObservableObject
{
    public ServerRegisterArea Area { get; set; }
    public int Address { get; set; }
    public ServerRegisterDataType DataType { get; set; }
    public ServerRegisterByteOrder ByteOrder { get; set; }
    public string InitialValue { get; set; } = "0";
    public string Note { get; set; } = string.Empty;

    public bool IsBit => DataType == ServerRegisterDataType.Bit;
    public bool IsNumeric => !IsBit;

    private string _currentValue = "0";
    public string CurrentValue
    {
        get => _currentValue;
        set
        {
            if (SetProperty(ref _currentValue, value))
                OnPropertyChanged(nameof(BitDisplayText));
        }
    }

    public string BitDisplayText =>
        CurrentValue.Equals("true", StringComparison.OrdinalIgnoreCase) || CurrentValue == "1"
            ? "ON (true)"
            : "OFF (false)";

    public ServerRegisterDefinition ToModel() => new()
    {
        Area = Area,
        Address = Address,
        DataType = DataType,
        ByteOrder = ByteOrder,
        InitialValue = InitialValue,
        Note = Note,
        CurrentValue = CurrentValue
    };
}

public sealed class ConnectionViewModel : ObservableObject
{
    private const long MaxCsvImportBytes = 5 * 1024 * 1024;
    private readonly CommunicationService _comm;
    private readonly ModbusServerService _server;
    private readonly PersistenceService _persistence;
    private List<ServerRegisterDefinition> _appliedDefinitions = new();

    public ConnectionViewModel(CommunicationService comm, ModbusServerService server, PersistenceService persistence)
    {
        _comm = comm;
        _server = server;
        _persistence = persistence;

        RefreshPorts();
        RefreshLocalIpAddresses();
        TcpServer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TcpServer.ListenPort))
                RefreshLocalIpAddresses();
        };
        LoadSavedServerConfig();

        _comm.StateChanged += (_, s) => RunOnUi(() =>
        {
            ChannelState = s;
            ChannelModeNotice = s == ChannelState.Connected && _comm.IsVirtualLoopback
                ? "提示：当前串口运行于内置虚拟环回仿真模式，未占用物理 COM 端口。"
                : string.Empty;
            NotifyVisibilityProperties();
        });
        _comm.ClientsChanged += (_, clients) => RunOnUi(() =>
        {
            ServerClients.Clear();
            foreach (var c in clients) ServerClients.Add(c);
        });
        _server.RegistersMutated += (_, _) => RunOnUi(SyncRuntimeValuesFromServer);
        _server.StatusChanged += (_, text) => RunOnUi(() =>
        {
            ServerStatusText = text;
            IsModbusServerRunning = _server.IsRunning;
        });
        _server.ClientsChanged += (_, clients) => RunOnUi(() =>
        {
            ModbusServerClients.Clear();
            foreach (var c in clients) ModbusServerClients.Add(c);
        });

        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        RefreshLocalIpAddressesCommand = new RelayCommand(RefreshLocalIpAddresses);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, ex => ConnectionError = ex.Message);
        DisconnectCommand = new AsyncRelayCommand(() => _comm.DisconnectAsync());
        KickClientCommand = new RelayCommand(p =>
        {
            if (p is TcpServerClientSession session)
                _comm.DisconnectServerClient(session.SessionId);
        });
        KickModbusServerClientCommand = new RelayCommand(p =>
        {
            if (p is TcpServerClientSession session)
                _server.DisconnectTcpClient(session.SessionId);
        });

        StartServerCommand = new AsyncRelayCommand(StartModbusServerAsync, ex => ServerBanner = $"启动失败: {ex.Message}");
        StopServerCommand = new AsyncRelayCommand(() => _server.StopAsync());
        AddRegisterCommand = new RelayCommand(AddRegisterDefinition);
        AddRegisterBatchCommand = new RelayCommand(AddRegisterDefinitionsBatch);
        DeleteRegisterCommand = new RelayCommand(p =>
        {
            if (p is ServerRegisterRowViewModel row)
            {
                ServerRegisters.Remove(row);
                MarkRegisterChangesPending("已暂存删除点位，请点击“应用点表”生效或“撤销修改”还原");
            }
        });
        ApplyRegistersCommand = new RelayCommand(ApplyStagedRegisters);
        RevertRegistersCommand = new RelayCommand(RevertStagedRegisters, () => HasPendingRegisterChanges);
        ApplyRuntimeValueCommand = new RelayCommand(p =>
        {
            if (p is ServerRegisterRowViewModel row)
                CommitRuntimeValue(row);
        });
        ToggleBitRuntimeCommand = new RelayCommand(p =>
        {
            if (p is ServerRegisterRowViewModel row && row.DataType == ServerRegisterDataType.Bit)
            {
                row.CurrentValue = row.CurrentValue.Equals("true", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
                CommitRuntimeValue(row);
            }
        });
        ResetRegistersCommand = new RelayCommand(() =>
        {
            _server.ResetToInitialValues();
            if (!HasPendingRegisterChanges)
                ReloadRegisterRows(_server.GetDefinitionsWithCurrentValues());
            else
                SyncRuntimeValuesFromServer();
            ServerBanner = "已全部恢复为初始值";
        });
        ExportConfigCommand = new RelayCommand(ExportServerConfig);
        ImportConfigCommand = new RelayCommand(ImportServerConfig);
        ExportCsvCommand = new RelayCommand(ExportRegistersCsv);
        ImportCsvCommand = new RelayCommand(ImportRegistersCsv);
    }

    public ObservableCollection<string> AvailablePorts { get; } = new();
    public ObservableCollection<string> BindAddressChoices { get; } = new() { "127.0.0.1", "0.0.0.0" };
    public ObservableCollection<TcpServerClientSession> ServerClients { get; } = new();
    public ObservableCollection<TcpServerClientSession> ModbusServerClients { get; } = new();
    public ObservableCollection<ServerRegisterRowViewModel> ServerRegisters { get; } = new();

    public IReadOnlyList<CommunicationType> CommunicationTypes { get; } = Enum.GetValues<CommunicationType>();
    public IReadOnlyList<int> BaudRates { get; } = new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400 };
    public IReadOnlyList<int> DataBitsOptions { get; } = new[] { 7, 8 };
    public IReadOnlyList<Parity> ParityOptions { get; } = Enum.GetValues<Parity>();
    public IReadOnlyList<StopBits> StopBitsOptions { get; } = new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two };
    public IReadOnlyList<ModbusServerTransportMode> ServerTransportModes { get; } = Enum.GetValues<ModbusServerTransportMode>();
    public IReadOnlyList<ServerRegisterArea> RegisterAreas { get; } = Enum.GetValues<ServerRegisterArea>();
    public IReadOnlyList<ServerRegisterDataType> RegisterDataTypes { get; } = Enum.GetValues<ServerRegisterDataType>();
    public IReadOnlyList<ServerRegisterByteOrder> RegisterByteOrders { get; } = Enum.GetValues<ServerRegisterByteOrder>();

    private CommunicationType _selectedType = CommunicationType.TcpClient;
    public CommunicationType SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value))
            {
                NotifyVisibilityProperties();
                if (IsTcpServerSelected)
                    RefreshLocalIpAddresses();
            }
        }
    }

    private ChannelState _channelState = ChannelState.Disconnected;
    public ChannelState ChannelState
    {
        get => _channelState;
        private set => SetProperty(ref _channelState, value);
    }

    public SerialPortParameters Serial { get; } = new();
    public TcpClientParameters TcpClient { get; } = new();
    public UdpParameters Udp { get; } = new();
    public TcpServerParameters TcpServer { get; } = new();

    public bool IsSerialSelected => SelectedType == CommunicationType.SerialPort;
    public bool IsTcpClientSelected => SelectedType == CommunicationType.TcpClient;
    public bool IsUdpSelected => SelectedType == CommunicationType.Udp;
    public bool IsTcpServerSelected => SelectedType == CommunicationType.TcpServer;

    private bool _allowClientVirtualLoopback = true;
    public bool AllowClientVirtualLoopback
    {
        get => _allowClientVirtualLoopback;
        set => SetProperty(ref _allowClientVirtualLoopback, value);
    }

    private bool _allowServerVirtualFallback = true;
    public bool AllowServerVirtualFallback
    {
        get => _allowServerVirtualFallback;
        set => SetProperty(ref _allowServerVirtualFallback, value);
    }

    private string _channelModeNotice = string.Empty;
    public string ChannelModeNotice
    {
        get => _channelModeNotice;
        private set => SetProperty(ref _channelModeNotice, value);
    }

    private string _localIpAddressesText = "未检测到可用 IPv4 地址";
    public string LocalIpAddressesText
    {
        get => _localIpAddressesText;
        private set => SetProperty(ref _localIpAddressesText, value);
    }

    private string _serverLocalIpAddressesText = "未检测到可用 IPv4 地址";
    public string ServerLocalIpAddressesText
    {
        get => _serverLocalIpAddressesText;
        private set => SetProperty(ref _serverLocalIpAddressesText, value);
    }

    private string _connectionError = string.Empty;
    public string ConnectionError
    {
        get => _connectionError;
        set => SetProperty(ref _connectionError, value);
    }

    private ModbusServerTransportMode _serverMode = ModbusServerTransportMode.Tcp;
    public ModbusServerTransportMode ServerMode
    {
        get => _serverMode;
        set
        {
            if (SetProperty(ref _serverMode, value))
            {
                OnPropertyChanged(nameof(ServerUsesTcp));
                OnPropertyChanged(nameof(ServerUsesSerial));
            }
        }
    }

    public bool ServerUsesTcp => ServerMode is ModbusServerTransportMode.Tcp or ModbusServerTransportMode.TcpAndSerial;
    public bool ServerUsesSerial => ServerMode is ModbusServerTransportMode.SerialRtu or ModbusServerTransportMode.SerialAscii or ModbusServerTransportMode.TcpAndSerial;

    private string _serverBindAddress = "127.0.0.1";
    public string ServerBindAddress
    {
        get => _serverBindAddress;
        set => SetProperty(ref _serverBindAddress, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim());
    }

    private int _serverPort = 5020;
    public int ServerPort
    {
        get => _serverPort;
        set
        {
            if (SetProperty(ref _serverPort, Math.Clamp(value, 1, 65535)))
                RefreshLocalIpAddresses();
        }
    }

    private int _serverStation = 1;
    public int ServerStation
    {
        get => _serverStation;
        set => SetProperty(ref _serverStation, Math.Clamp(value, 1, 247));
    }

    private int _serverMaxClients = 32;
    public int ServerMaxClients
    {
        get => _serverMaxClients;
        set => SetProperty(ref _serverMaxClients, Math.Clamp(value, 1, 256));
    }

    private int _serverIdleTimeoutMs = 60000;
    public int ServerIdleTimeoutMs
    {
        get => _serverIdleTimeoutMs;
        set => SetProperty(ref _serverIdleTimeoutMs, Math.Clamp(value, 0, 3600000));
    }

    private int _serverMaxFrameLength = 260;
    public int ServerMaxFrameLength
    {
        get => _serverMaxFrameLength;
        set => SetProperty(ref _serverMaxFrameLength, Math.Clamp(value, 16, 4096));
    }

    private int _serverSendQueueLimit = 64;
    public int ServerSendQueueLimit
    {
        get => _serverSendQueueLimit;
        set => SetProperty(ref _serverSendQueueLimit, Math.Clamp(value, 1, 1024));
    }

    private string _serverAllowedClientIps = string.Empty;
    public string ServerAllowedClientIps
    {
        get => _serverAllowedClientIps;
        set => SetProperty(ref _serverAllowedClientIps, value ?? string.Empty);
    }

    public SerialPortParameters ServerSerial { get; } = new();

    private bool _isModbusServerRunning;
    public bool IsModbusServerRunning
    {
        get => _isModbusServerRunning;
        private set => SetProperty(ref _isModbusServerRunning, value);
    }

    private string _serverStatusText = "服务器已停止";
    public string ServerStatusText
    {
        get => _serverStatusText;
        private set => SetProperty(ref _serverStatusText, value);
    }

    private string _serverBanner = string.Empty;
    public string ServerBanner
    {
        get => _serverBanner;
        set => SetProperty(ref _serverBanner, value);
    }

    private bool _hasPendingRegisterChanges;
    public bool HasPendingRegisterChanges
    {
        get => _hasPendingRegisterChanges;
        private set
        {
            if (SetProperty(ref _hasPendingRegisterChanges, value))
                RevertRegistersCommand?.RaiseCanExecuteChanged();
        }
    }

    private ServerRegisterArea _newArea = ServerRegisterArea.HoldingRegister;
    public ServerRegisterArea NewArea
    {
        get => _newArea;
        set
        {
            if (SetProperty(ref _newArea, value))
            {
                if (_newArea is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput)
                {
                    NewDataType = ServerRegisterDataType.Bit;
                    if (NewInitialValue == "0") NewInitialValue = "false";
                }
                else if (NewDataType == ServerRegisterDataType.Bit)
                {
                    NewDataType = ServerRegisterDataType.UInt16;
                    if (NewInitialValue is "false" or "true") NewInitialValue = "0";
                }
            }
        }
    }

    private int _newAddress;
    public int NewAddress { get => _newAddress; set => SetProperty(ref _newAddress, Math.Clamp(value, 0, 65535)); }

    private int _newRegisterCount = 10;
    public int NewRegisterCount
    {
        get => _newRegisterCount;
        set => SetProperty(ref _newRegisterCount, Math.Clamp(value, 1, 1000));
    }

    private ServerRegisterDataType _newDataType = ServerRegisterDataType.UInt16;
    public ServerRegisterDataType NewDataType { get => _newDataType; set => SetProperty(ref _newDataType, value); }

    private ServerRegisterByteOrder _newByteOrder = ServerRegisterByteOrder.BigEndian;
    public ServerRegisterByteOrder NewByteOrder { get => _newByteOrder; set => SetProperty(ref _newByteOrder, value); }

    private string _newInitialValue = "0";
    public string NewInitialValue { get => _newInitialValue; set => SetProperty(ref _newInitialValue, value); }

    private string _newNote = string.Empty;
    public string NewNote { get => _newNote; set => SetProperty(ref _newNote, value); }

    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand RefreshLocalIpAddressesCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand KickClientCommand { get; }
    public RelayCommand KickModbusServerClientCommand { get; }
    public AsyncRelayCommand StartServerCommand { get; }
    public AsyncRelayCommand StopServerCommand { get; }
    public RelayCommand AddRegisterCommand { get; }
    public RelayCommand AddRegisterBatchCommand { get; }
    public RelayCommand DeleteRegisterCommand { get; }
    public RelayCommand ApplyRegistersCommand { get; }
    public RelayCommand RevertRegistersCommand { get; }
    public RelayCommand ApplyRuntimeValueCommand { get; }
    public RelayCommand ToggleBitRuntimeCommand { get; }
    public RelayCommand ResetRegistersCommand { get; }
    public RelayCommand ExportConfigCommand { get; }
    public RelayCommand ImportConfigCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ImportCsvCommand { get; }

    public async Task ApplyDeviceProfileAsync(DeviceProfile profile, bool autoConnect)
    {
        ConnectionError = string.Empty;
        try
        {
            string protocol = profile.Protocol?.Trim() ?? "Modbus TCP";
            string endpoint = profile.Endpoint?.Trim() ?? string.Empty;

            if (protocol.Contains("RTU", StringComparison.OrdinalIgnoreCase) ||
                protocol.Contains("ASCII", StringComparison.OrdinalIgnoreCase))
            {
                SelectedType = CommunicationType.SerialPort;
                var parts = endpoint.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    Serial.PortName = parts[0];
                    if (!AvailablePorts.Contains(parts[0]))
                        AvailablePorts.Add(parts[0]);
                }
                if (parts.Length >= 2 && int.TryParse(parts[1], out int baud))
                    Serial.BaudRate = baud;
            }
            else if (protocol.Contains("TCP Server", StringComparison.OrdinalIgnoreCase))
            {
                SelectedType = CommunicationType.TcpServer;
                if (TryParseHostAndPort(endpoint, 6000, out string host, out int listenPort))
                {
                    if (IPAddress.TryParse(host, out _))
                        TcpServer.BindAddress = host;
                    TcpServer.ListenPort = listenPort;
                }
            }
            else if (protocol.Contains("UDP", StringComparison.OrdinalIgnoreCase))
            {
                SelectedType = CommunicationType.Udp;
                if (TryParseHostAndPort(endpoint, 502, out string host, out int port))
                {
                    Udp.RemoteHost = host;
                    Udp.RemotePort = port;
                }
            }
            else
            {
                SelectedType = CommunicationType.TcpClient;
                if (TryParseHostAndPort(endpoint, 5020, out string host, out int port))
                {
                    TcpClient.Host = host;
                    TcpClient.Port = port;
                }
            }

            if (autoConnect)
                await ConnectAsync();
        }
        catch (Exception ex)
        {
            ConnectionError = $"应用设备方案失败: {ex.Message}";
        }
    }

    private static bool TryParseHostAndPort(string endpoint, int defaultPort, out string host, out int port)
    {
        host = "127.0.0.1";
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        int colonIdx = endpoint.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(endpoint[(colonIdx + 1)..], out int parsedPort) && parsedPort is >= 1 and <= 65535)
        {
            host = endpoint[..colonIdx].Trim();
            port = parsedPort;
            return true;
        }

        if (int.TryParse(endpoint, out int portOnly) && portOnly is >= 1 and <= 65535)
        {
            port = portOnly;
            return true;
        }

        host = endpoint;
        return true;
    }

    public void RefreshLocalIpAddresses()
    {
        var rawAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                try
                {
                    foreach (var address in network.GetIPProperties().UnicastAddresses
                        .Select(address => address.Address)
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)))
                    {
                        rawAddresses.Add(address.ToString());
                    }
                }
                catch (NetworkInformationException)
                {
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            LocalIpAddressesText = $"读取失败：{ex.Message}";
            ServerLocalIpAddressesText = LocalIpAddressesText;
            return;
        }

        foreach (string ip in rawAddresses.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            if (!BindAddressChoices.Contains(ip))
                BindAddressChoices.Add(ip);
        }

        if (rawAddresses.Count > 0)
        {
            var sorted = rawAddresses.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            LocalIpAddressesText = string.Join("  |  ", sorted.Select(a => $"{a}:{TcpServer.ListenPort}"));
            ServerLocalIpAddressesText = string.Join("  |  ", sorted.Select(a => $"{a}:{ServerPort}"));
        }
        else
        {
            LocalIpAddressesText = $"127.0.0.1:{TcpServer.ListenPort}";
            ServerLocalIpAddressesText = $"127.0.0.1:{ServerPort}";
        }
    }

    public void RefreshPorts()
    {
        AvailablePorts.Clear();
        try
        {
            foreach (var p in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                AvailablePorts.Add(p);
        }
        catch { /* ignore on non-Windows host */ }

        if (AvailablePorts.Count == 0)
        {
            AvailablePorts.Add("COM1");
            AvailablePorts.Add("COM2");
            AvailablePorts.Add("COM3");
        }
        if (!AvailablePorts.Contains(Serial.PortName))
            Serial.PortName = AvailablePorts[0];
        if (!AvailablePorts.Contains(ServerSerial.PortName))
            ServerSerial.PortName = AvailablePorts[0];
    }

    private async Task ConnectAsync()
    {
        ConnectionError = string.Empty;
        try
        {
            switch (SelectedType)
            {
                case CommunicationType.SerialPort:
                    if (string.IsNullOrWhiteSpace(Serial.PortName))
                        throw new ArgumentException("请输入或选择串口号");
                    await _comm.ConnectSerialAsync(Serial, _server, AllowClientVirtualLoopback);
                    break;
                case CommunicationType.TcpClient:
                    if (string.IsNullOrWhiteSpace(TcpClient.Host))
                        throw new ArgumentException("目标主机地址不能为空");
                    if (TcpClient.Port is < 1 or > 65535)
                        throw new ArgumentOutOfRangeException(nameof(TcpClient.Port), "TCP 端口必须在 1 至 65535 之间");
                    await _comm.ConnectTcpClientAsync(TcpClient);
                    break;
                case CommunicationType.Udp:
                    if (string.IsNullOrWhiteSpace(Udp.RemoteHost))
                        throw new ArgumentException("UDP 远端主机不能为空");
                    if (Udp.RemotePort is < 1 or > 65535)
                        throw new ArgumentOutOfRangeException(nameof(Udp.RemotePort), "UDP 远端端口必须在 1 至 65535 之间");
                    await _comm.ConnectUdpAsync(Udp);
                    break;
                case CommunicationType.TcpServer:
                    if (TcpServer.ListenPort is < 1 or > 65535)
                        throw new ArgumentOutOfRangeException(nameof(TcpServer.ListenPort), "监听端口必须在 1 至 65535 之间");
                    await _comm.StartTcpServerAsync(TcpServer);
                    break;
            }
        }
        catch (Exception ex)
        {
            ConnectionError = ex.Message;
        }
    }

    private async Task StartModbusServerAsync()
    {
        ServerBanner = string.Empty;
        try
        {
            if (HasPendingRegisterChanges)
                ApplyRegistersToServerAndSave();
            else
                SaveServerConfig();

            var security = BuildModbusServerSecurityOptions();
            await _server.StartAsync(ServerPort, ServerStation, ServerMode, ServerSerial, AllowServerVirtualFallback, security);
            SaveServerConfig();
        }
        catch (Exception ex)
        {
            ServerBanner = $"启动失败: {ex.Message}";
        }
    }

    private ModbusServerSecurityOptions BuildModbusServerSecurityOptions() => new()
    {
        BindAddress = ServerBindAddress,
        MaxClients = ServerMaxClients,
        IdleTimeoutMs = ServerIdleTimeoutMs,
        MaxFrameLength = ServerMaxFrameLength,
        SendQueueLimit = ServerSendQueueLimit,
        AllowedClientIps = ServerAllowedClientIps
    };

    private void AddRegisterDefinition()
    {
        ServerBanner = string.Empty;
        try
        {
            var candidate = new ServerRegisterDefinition
            {
                Area = NewArea,
                Address = NewAddress,
                DataType = NewDataType,
                ByteOrder = NewByteOrder,
                InitialValue = NewInitialValue,
                CurrentValue = NewInitialValue,
                Note = NewNote.Trim()
            };
            ServerRegisterCodec.Validate(candidate);

            var all = ServerRegisters.Select(r => r.ToModel()).ToList();
            int existingIdx = all.FindIndex(d => d.Area == candidate.Area && d.Address == candidate.Address);
            if (existingIdx >= 0) all[existingIdx] = candidate;
            else all.Add(candidate);

            ServerRegisterCodec.ValidateNoOverlap(all);
            ReloadRegisterRows(all);
            NewAddress += ServerRegisterCodec.GetWidth(candidate.DataType);
            NewNote = string.Empty;
            MarkRegisterChangesPending("已暂存 1 条点位定义，点击“应用点表”生效或“撤销修改”还原");
        }
        catch (Exception ex)
        {
            ServerBanner = $"定义错误: {ex.Message}";
        }
    }

    private void AddRegisterDefinitionsBatch()
    {
        ServerBanner = string.Empty;
        try
        {
            var all = ServerRegisters.Select(r => r.ToModel()).ToList();
            int address = NewAddress;
            int width = ServerRegisterCodec.GetWidth(NewDataType);

            for (int i = 0; i < NewRegisterCount; i++)
            {
                var candidate = new ServerRegisterDefinition
                {
                    Area = NewArea,
                    Address = address,
                    DataType = NewDataType,
                    ByteOrder = NewByteOrder,
                    InitialValue = NewInitialValue,
                    CurrentValue = NewInitialValue,
                    Note = string.IsNullOrWhiteSpace(NewNote) ? string.Empty : $"{NewNote.Trim()} {i + 1}"
                };

                ServerRegisterCodec.Validate(candidate);
                all.Add(candidate);
                address = checked(address + width);
            }

            ServerRegisterCodec.ValidateNoOverlap(all);
            ReloadRegisterRows(all);
            NewAddress = address;
            NewNote = string.Empty;
            MarkRegisterChangesPending($"已暂存批量添加 {NewRegisterCount} 条定义，点击“应用点表”生效");
        }
        catch (Exception ex)
        {
            ServerBanner = $"批量定义错误: {ex.Message}";
        }
    }

    public void CommitRuntimeValue(ServerRegisterRowViewModel row)
    {
        ServerBanner = string.Empty;
        try
        {
            if (!_appliedDefinitions.Any(d => d.Area == row.Area && d.Address == row.Address))
            {
                ServerBanner = "该点位尚未应用到运行实例，请先点击“应用点表”";
                return;
            }

            _server.SetRuntimeValue(row.Area, row.Address, row.CurrentValue);
            ServerBanner = $"已在线更新 {row.Area} @ {row.Address} = {row.CurrentValue}";
        }
        catch (Exception ex)
        {
            ServerBanner = $"在线修改失败: {ex.Message}";
            SyncRuntimeValuesFromServer();
        }
    }

    private void ApplyStagedRegisters()
    {
        ServerBanner = string.Empty;
        try
        {
            ApplyRegistersToServerAndSave();
            ServerBanner = $"已应用并保存 {ServerRegisters.Count} 条点表定义";
        }
        catch (Exception ex)
        {
            ServerBanner = $"应用点表失败: {ex.Message}";
        }
    }

    private void RevertStagedRegisters()
    {
        var liveDefs = _server.GetDefinitionsWithCurrentValues();
        var fallback = liveDefs.Count > 0 || _appliedDefinitions.Count == 0
            ? liveDefs
            : _appliedDefinitions;
        ReloadRegisterRows(fallback);
        HasPendingRegisterChanges = false;
        ServerBanner = "已撤销未应用的点表修改";
    }

    private void MarkRegisterChangesPending(string message)
    {
        HasPendingRegisterChanges = true;
        ServerBanner = message;
    }

    private void ApplyRegistersToServerAndSave()
    {
        var list = ServerRegisters.Select(r => r.ToModel()).ToList();
        _server.ConfigureRegisters(list);
        _appliedDefinitions = _server.GetDefinitionsWithCurrentValues();
        ReloadRegisterRows(_appliedDefinitions);
        HasPendingRegisterChanges = false;
        SaveServerConfig();
    }

    private void LoadSavedServerConfig()
    {
        var cfg = _persistence.LoadServerConfig();
        ApplyLoadedConfigToProperties(cfg);

        try
        {
            _server.ConfigureRegisters(cfg.Registers);
            _appliedDefinitions = _server.GetDefinitionsWithCurrentValues();
            ReloadRegisterRows(_appliedDefinitions);
            HasPendingRegisterChanges = false;
        }
        catch
        {
            _appliedDefinitions = CloneDefinitions(cfg.Registers);
            ReloadRegisterRows(cfg.Registers);
        }
        RefreshLocalIpAddresses();
    }

    private void ApplyLoadedConfigToProperties(ModbusServerConfiguration cfg)
    {
        ServerBindAddress = string.IsNullOrWhiteSpace(cfg.BindAddress) ? "127.0.0.1" : cfg.BindAddress;
        if (!BindAddressChoices.Contains(ServerBindAddress))
            BindAddressChoices.Add(ServerBindAddress);

        ServerPort = cfg.Port;
        ServerStation = cfg.Station;
        ServerMaxClients = cfg.MaxClients > 0 ? cfg.MaxClients : 32;
        ServerIdleTimeoutMs = cfg.IdleTimeoutMs >= 0 ? cfg.IdleTimeoutMs : 60000;
        ServerMaxFrameLength = cfg.MaxFrameLength >= 16 ? cfg.MaxFrameLength : 260;
        ServerSendQueueLimit = cfg.SendQueueLimit > 0 ? cfg.SendQueueLimit : 64;
        ServerAllowedClientIps = cfg.AllowedClientIps ?? string.Empty;
        ServerMode = cfg.ServerMode;
        ServerSerial.PortName = cfg.ServerSerial.PortName;
        ServerSerial.BaudRate = cfg.ServerSerial.BaudRate;
        ServerSerial.DataBits = cfg.ServerSerial.DataBits;
        ServerSerial.Parity = cfg.ServerSerial.Parity;
        ServerSerial.StopBits = cfg.ServerSerial.StopBits;
    }

    private ModbusServerConfiguration BuildPersistedServerConfig(bool useAppliedRegisters) => new()
    {
        Version = WorkspaceConfiguration.CurrentSchemaVersion,
        BindAddress = ServerBindAddress,
        Port = ServerPort,
        Station = ServerStation,
        MaxClients = ServerMaxClients,
        IdleTimeoutMs = ServerIdleTimeoutMs,
        MaxFrameLength = ServerMaxFrameLength,
        SendQueueLimit = ServerSendQueueLimit,
        AllowedClientIps = ServerAllowedClientIps,
        ServerMode = ServerMode,
        ServerSerial = ServerSerial.Clone(),
        Registers = useAppliedRegisters
            ? CloneDefinitions(_appliedDefinitions)
            : ServerRegisters.Select(r => r.ToModel()).ToList()
    };

    private void SaveServerConfig()
    {
        _persistence.SaveServerConfig(BuildPersistedServerConfig(useAppliedRegisters: true));
    }

    private static List<ServerRegisterDefinition> CloneDefinitions(IEnumerable<ServerRegisterDefinition> source) =>
        source.Select(d => new ServerRegisterDefinition
        {
            Area = d.Area,
            Address = d.Address,
            DataType = d.DataType,
            ByteOrder = d.ByteOrder,
            InitialValue = d.InitialValue,
            Note = d.Note,
            CurrentValue = d.CurrentValue
        }).ToList();

    private void ReloadRegisterRows(IEnumerable<ServerRegisterDefinition> defs)
    {
        ServerRegisters.Clear();
        foreach (var d in defs)
        {
            ServerRegisters.Add(new ServerRegisterRowViewModel
            {
                Area = d.Area,
                Address = d.Address,
                DataType = d.DataType,
                ByteOrder = d.ByteOrder,
                InitialValue = d.InitialValue,
                Note = d.Note,
                CurrentValue = d.CurrentValue
            });
        }
    }

    private void SyncRuntimeValuesFromServer()
    {
        var map = _server.GetDefinitionsWithCurrentValues()
            .ToDictionary(d => (d.Area, d.Address), d => d.CurrentValue);

        foreach (var row in ServerRegisters)
        {
            if (map.TryGetValue((row.Area, row.Address), out var current))
                row.CurrentValue = current;
        }
    }

    private void ExportServerConfig()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"modbus-server-config-{DateTime.Now:yyyyMMdd}.json"
        };
        if (dlg.ShowDialog() == true)
        {
            var cfg = BuildPersistedServerConfig(useAppliedRegisters: false);
            File.WriteAllText(dlg.FileName, PersistenceService.SerializeServerConfig(cfg), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ServerBanner = $"已导出配置: {dlg.FileName}";
        }
    }

    private void ImportServerConfig()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var info = new FileInfo(dlg.FileName);
                if (info.Length > MaxCsvImportBytes)
                    throw new InvalidDataException("配置文件大小超过 5 MB 限制");

                string content = ReadUtf8File(dlg.FileName);
                var cfg = PersistenceService.DeserializeServerConfig(content)
                    ?? throw new InvalidDataException("配置文件格式无效");

                foreach (var def in cfg.Registers)
                    ServerRegisterCodec.Validate(def);
                ServerRegisterCodec.ValidateNoOverlap(cfg.Registers);

                ApplyLoadedConfigToProperties(cfg);
                ReloadRegisterRows(cfg.Registers);
                MarkRegisterChangesPending($"已载入 JSON 配置 ({cfg.Registers.Count} 项)，点击“应用点表”生效或“撤销修改”还原");
            }
            catch (Exception ex)
            {
                ServerBanner = $"导入失败: {ex.Message}";
            }
        }
    }

    private void ExportRegistersCsv()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 点表文件 (*.csv)|*.csv",
            FileName = $"modbus-point-table-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Area,Address,DataType,ByteOrder,InitialValue,CurrentValue,Note");
        foreach (var row in ServerRegisters)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.Area.ToString()),
                row.Address.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(row.DataType.ToString()),
                EscapeCsv(row.ByteOrder.ToString()),
                EscapeCsv(row.InitialValue),
                EscapeCsv(row.CurrentValue),
                EscapeCsv(row.Note)));
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        ServerBanner = $"已导出点表 CSV ({ServerRegisters.Count} 项): {dlg.FileName}";
    }

    private void ImportRegistersCsv()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV 点表文件 (*.csv)|*.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var fileInfo = new FileInfo(dlg.FileName);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("CSV 文件不存在", dlg.FileName);
            if (fileInfo.Length > MaxCsvImportBytes)
                throw new InvalidDataException($"CSV 文件过大 ({fileInfo.Length / 1024.0 / 1024.0:0.##} MB)，限制 5 MB 以内");

            string rawText = ReadUtf8File(dlg.FileName);
            var imported = ParseRegisterDefinitionsFromCsv(rawText);
            ReloadRegisterRows(imported);
            MarkRegisterChangesPending($"已从 CSV 载入 {imported.Count} 项待应用点位，请点击“应用点表”生效或“撤销修改”还原");
        }
        catch (Exception ex)
        {
            ServerBanner = $"CSV 导入失败: {ex.Message}";
        }
    }

    public static List<ServerRegisterDefinition> ParseRegisterDefinitionsFromCsv(string csvText)
    {
        var records = ParseCsvRecords(csvText)
            .Where(cols => cols.Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();
        if (records.Count == 0)
            throw new InvalidDataException("CSV 文件为空");

        var imported = new List<ServerRegisterDefinition>();
        int startIndex = records[0].Count > 0 && records[0][0].Equals("Area", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

        for (int i = startIndex; i < records.Count; i++)
        {
            var cols = records[i];
            if (cols.Count < 5)
                throw new FormatException($"第 {i + 1} 条记录列数不足（至少需要 Area,Address,DataType,ByteOrder,InitialValue）");

            if (!Enum.TryParse<ServerRegisterArea>(cols[0], ignoreCase: true, out var area))
                throw new FormatException($"第 {i + 1} 条记录存储区无效: {cols[0]}");
            if (!int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int address))
                throw new FormatException($"第 {i + 1} 条记录地址无效: {cols[1]}");
            if (!Enum.TryParse<ServerRegisterDataType>(cols[2], ignoreCase: true, out var dataType))
                throw new FormatException($"第 {i + 1} 条记录数据类型无效: {cols[2]}");
            if (!Enum.TryParse<ServerRegisterByteOrder>(cols[3], ignoreCase: true, out var byteOrder))
                throw new FormatException($"第 {i + 1} 条记录字节序无效: {cols[3]}");

            string initial = cols[4];
            string note = cols.Count >= 7 ? cols[6] : (cols.Count == 6 ? cols[5] : string.Empty);

            var def = new ServerRegisterDefinition
            {
                Area = area,
                Address = address,
                DataType = dataType,
                ByteOrder = byteOrder,
                InitialValue = initial,
                CurrentValue = initial,
                Note = note
            };
            ServerRegisterCodec.Validate(def);
            imported.Add(def);
        }

        if (imported.Count == 0)
            throw new InvalidDataException("CSV 文件中不包含有效点表记录");

        ServerRegisterCodec.ValidateNoOverlap(imported);
        return imported;
    }

    private static string ReadUtf8File(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string EscapeCsv(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    public static List<List<string>> ParseCsvRecords(string csvText)
    {
        var records = new List<List<string>>();
        if (string.IsNullOrEmpty(csvText))
            return records;

        string normalized = csvText.TrimStart('\uFEFF');
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < normalized.Length && normalized[i + 1] == '"')
                {
                    currentField.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    currentField.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                currentRow.Add(currentField.ToString().Trim());
                currentField.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < normalized.Length && normalized[i + 1] == '\n')
                    i++;

                currentRow.Add(currentField.ToString().Trim());
                currentField.Clear();
                records.Add(currentRow);
                currentRow = new List<string>();
            }
            else
            {
                currentField.Append(c);
            }
        }

        if (inQuotes)
            throw new FormatException("CSV 引号未正确闭合");

        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString().Trim());
            records.Add(currentRow);
        }

        return records;
    }

    private void NotifyVisibilityProperties()
    {
        OnPropertyChanged(nameof(IsSerialSelected));
        OnPropertyChanged(nameof(IsTcpClientSelected));
        OnPropertyChanged(nameof(IsUdpSelected));
        OnPropertyChanged(nameof(IsTcpServerSelected));
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
