using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
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

    private string _currentValue = "0";
    public string CurrentValue
    {
        get => _currentValue;
        set => SetProperty(ref _currentValue, value);
    }

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
    private readonly CommunicationService _comm;
    private readonly ModbusServerService _server;
    private readonly PersistenceService _persistence;

    public ConnectionViewModel(CommunicationService comm, ModbusServerService server, PersistenceService persistence)
    {
        _comm = comm;
        _server = server;
        _persistence = persistence;

        RefreshPorts();
        LoadSavedServerConfig();

        _comm.StateChanged += (_, s) => RunOnUi(() =>
        {
            ChannelState = s;
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

        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(() => _comm.DisconnectAsync());
        KickClientCommand = new RelayCommand(p =>
        {
            if (p is TcpServerClientSession session)
                _comm.DisconnectServerClient(session.SessionId);
        });

        StartServerCommand = new AsyncRelayCommand(StartModbusServerAsync);
        StopServerCommand = new AsyncRelayCommand(() => _server.StopAsync());
        AddRegisterCommand = new RelayCommand(AddRegisterDefinition);
        DeleteRegisterCommand = new RelayCommand(p =>
        {
            if (p is ServerRegisterRowViewModel row)
            {
                ServerRegisters.Remove(row);
                ApplyRegistersToServerAndSave();
            }
        });
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
            ServerBanner = "已全部重置为初始值";
        });
        ExportConfigCommand = new RelayCommand(ExportServerConfig);
        ImportConfigCommand = new RelayCommand(ImportServerConfig);
    }

    public ObservableCollection<string> AvailablePorts { get; } = new();
    public ObservableCollection<TcpServerClientSession> ServerClients { get; } = new();
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
                NotifyVisibilityProperties();
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

    private string _connectionError = string.Empty;
    public string ConnectionError
    {
        get => _connectionError;
        set => SetProperty(ref _connectionError, value);
    }

    // Modbus Server settings
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

    private int _serverPort = 5020;
    public int ServerPort { get => _serverPort; set => SetProperty(ref _serverPort, value); }

    private int _serverStation = 1;
    public int ServerStation { get => _serverStation; set => SetProperty(ref _serverStation, value); }

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

    // New Register Definition draft
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
    public int NewAddress { get => _newAddress; set => SetProperty(ref _newAddress, value); }

    private ServerRegisterDataType _newDataType = ServerRegisterDataType.UInt16;
    public ServerRegisterDataType NewDataType { get => _newDataType; set => SetProperty(ref _newDataType, value); }

    private ServerRegisterByteOrder _newByteOrder = ServerRegisterByteOrder.BigEndian;
    public ServerRegisterByteOrder NewByteOrder { get => _newByteOrder; set => SetProperty(ref _newByteOrder, value); }

    private string _newInitialValue = "0";
    public string NewInitialValue { get => _newInitialValue; set => SetProperty(ref _newInitialValue, value); }

    private string _newNote = string.Empty;
    public string NewNote { get => _newNote; set => SetProperty(ref _newNote, value); }

    public RelayCommand RefreshPortsCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand KickClientCommand { get; }
    public AsyncRelayCommand StartServerCommand { get; }
    public AsyncRelayCommand StopServerCommand { get; }
    public RelayCommand AddRegisterCommand { get; }
    public RelayCommand DeleteRegisterCommand { get; }
    public RelayCommand ApplyRuntimeValueCommand { get; }
    public RelayCommand ToggleBitRuntimeCommand { get; }
    public RelayCommand ResetRegistersCommand { get; }
    public RelayCommand ExportConfigCommand { get; }
    public RelayCommand ImportConfigCommand { get; }

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
                    await _comm.ConnectSerialAsync(Serial);
                    break;
                case CommunicationType.TcpClient:
                    await _comm.ConnectTcpClientAsync(TcpClient);
                    break;
                case CommunicationType.Udp:
                    await _comm.ConnectUdpAsync(Udp);
                    break;
                case CommunicationType.TcpServer:
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
            ApplyRegistersToServerAndSave();
            await _server.StartAsync(ServerPort, ServerStation, ServerMode, ServerSerial);
            SaveServerConfig();
        }
        catch (Exception ex)
        {
            ServerBanner = $"启动失败: {ex.Message}";
        }
    }

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
            _server.ConfigureRegisters(all);
            ReloadRegisterRows(_server.GetDefinitionsWithCurrentValues());
            SaveServerConfig();
            NewAddress += ServerRegisterCodec.GetWidth(candidate.DataType);
            NewNote = string.Empty;
            ServerBanner = "已添加并同步到服务器内存";
        }
        catch (Exception ex)
        {
            ServerBanner = $"定义错误: {ex.Message}";
        }
    }

    public void CommitRuntimeValue(ServerRegisterRowViewModel row)
    {
        ServerBanner = string.Empty;
        try
        {
            _server.SetRuntimeValue(row.Area, row.Address, row.CurrentValue);
            ServerBanner = $"已在线更新 {row.Area} @ {row.Address} = {row.CurrentValue}";
        }
        catch (Exception ex)
        {
            ServerBanner = $"在线修改失败: {ex.Message}";
            SyncRuntimeValuesFromServer();
        }
    }

    private void ApplyRegistersToServerAndSave()
    {
        var list = ServerRegisters.Select(r => r.ToModel()).ToList();
        _server.ConfigureRegisters(list);
        ReloadRegisterRows(_server.GetDefinitionsWithCurrentValues());
        SaveServerConfig();
    }

    private void LoadSavedServerConfig()
    {
        var cfg = _persistence.LoadServerConfig();
        ServerPort = cfg.Port;
        ServerStation = cfg.Station;
        ServerMode = cfg.ServerMode;
        ServerSerial.PortName = cfg.ServerSerial.PortName;
        ServerSerial.BaudRate = cfg.ServerSerial.BaudRate;
        ServerSerial.DataBits = cfg.ServerSerial.DataBits;
        ServerSerial.Parity = cfg.ServerSerial.Parity;
        ServerSerial.StopBits = cfg.ServerSerial.StopBits;

        try
        {
            _server.ConfigureRegisters(cfg.Registers);
            ReloadRegisterRows(_server.GetDefinitionsWithCurrentValues());
        }
        catch
        {
            ReloadRegisterRows(cfg.Registers);
        }
    }

    private void SaveServerConfig()
    {
        var cfg = new ModbusServerConfiguration
        {
            Port = ServerPort,
            Station = ServerStation,
            ServerMode = ServerMode,
            ServerSerial = ServerSerial.Clone(),
            Registers = ServerRegisters.Select(r => r.ToModel()).ToList()
        };
        _persistence.SaveServerConfig(cfg);
    }

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
        var latest = _server.GetDefinitionsWithCurrentValues();
        for (int i = 0; i < latest.Count && i < ServerRegisters.Count; i++)
        {
            if (ServerRegisters[i].Area == latest[i].Area && ServerRegisters[i].Address == latest[i].Address)
                ServerRegisters[i].CurrentValue = latest[i].CurrentValue;
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
            var cfg = new ModbusServerConfiguration
            {
                Port = ServerPort,
                Station = ServerStation,
                ServerMode = ServerMode,
                ServerSerial = ServerSerial.Clone(),
                Registers = ServerRegisters.Select(r => r.ToModel()).ToList()
            };
            File.WriteAllText(dlg.FileName, PersistenceService.SerializeServerConfig(cfg));
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
                var cfg = PersistenceService.DeserializeServerConfig(File.ReadAllText(dlg.FileName))
                    ?? throw new InvalidDataException("配置文件格式无效");
                ServerPort = cfg.Port;
                ServerStation = cfg.Station;
                ServerMode = cfg.ServerMode;
                ServerSerial.PortName = cfg.ServerSerial.PortName;
                ServerSerial.BaudRate = cfg.ServerSerial.BaudRate;
                ServerSerial.DataBits = cfg.ServerSerial.DataBits;
                ServerSerial.Parity = cfg.ServerSerial.Parity;
                ServerSerial.StopBits = cfg.ServerSerial.StopBits;

                _server.ConfigureRegisters(cfg.Registers);
                ReloadRegisterRows(_server.GetDefinitionsWithCurrentValues());
                SaveServerConfig();
                ServerBanner = $"已导入配置 ({cfg.Registers.Count} 项)";
            }
            catch (Exception ex)
            {
                ServerBanner = $"导入失败: {ex.Message}";
            }
        }
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
