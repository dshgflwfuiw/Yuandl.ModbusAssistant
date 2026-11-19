using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Enums;
using Yuandl.ThemeUI.Extensions;
using Yuandl.ThemeUI.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

/// <summary>
/// 通讯连接页面 ViewModel
/// 负责串口 (RS232/RS485)、TCP Client、UDP 等通讯链路的参数配置、端口探测与连接管理
/// </summary>
public partial class ConnectionViewModel : ViewModelBase
{
    private readonly ICommunicationService _communicationService;
    private readonly ISnackbarService _snackbarService;
    private readonly HslModbusServerService? _serverService;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string ConfigurationPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yuandl.ModbusAssistant", "modbus-server.json");

    [ObservableProperty] private int serverPort = 5020;
    [ObservableProperty] private byte serverStation = 1;
    [ObservableProperty] private bool isServerRunning;
    [ObservableProperty] private string serverStatusText = "服务器已停止";
    [ObservableProperty] private ObservableCollection<ServerRegisterDefinition> serverRegisters = [];
    [ObservableProperty] private ServerRegisterDefinition? selectedServerRegister;
    [ObservableProperty] private ServerRegisterArea selectedRegisterArea = ServerRegisterArea.HoldingRegister;
    [ObservableProperty] private ServerRegisterDataType selectedRegisterDataType = ServerRegisterDataType.UInt16;
    [ObservableProperty] private ServerRegisterByteOrder selectedRegisterByteOrder = ServerRegisterByteOrder.BigEndian;
    [ObservableProperty] private ushort serverRegisterAddress;
    [ObservableProperty] private string serverRegisterValue = "0";
    [ObservableProperty] private string serverRegisterNote = string.Empty;

    public ServerRegisterArea[] RegisterAreaOptions { get; } = Enum.GetValues<ServerRegisterArea>();
    public ServerRegisterDataType[] RegisterDataTypeOptions { get; } = Enum.GetValues<ServerRegisterDataType>();
    public ServerRegisterByteOrder[] RegisterByteOrderOptions { get; } = Enum.GetValues<ServerRegisterByteOrder>();

    partial void OnSelectedRegisterAreaChanged(ServerRegisterArea value)
    {
        var bitArea = value is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput;
        if (bitArea) SelectedRegisterDataType = ServerRegisterDataType.Bit;
        else if (SelectedRegisterDataType == ServerRegisterDataType.Bit) SelectedRegisterDataType = ServerRegisterDataType.UInt16;
    }

    // 通讯类型选择: 0 = 串口, 1 = TCP Client, 2 = UDP
    [ObservableProperty]
    private int selectedCommTypeIndex = 0;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string connectionStateText = "未连接";

    // ====== 串口参数 ======
    [ObservableProperty]
    private ObservableCollection<string> availablePorts = [];

    [ObservableProperty]
    private string selectedPort = "COM1";

    [ObservableProperty]
    private int selectedBaudRate = 9600;

    public int[] BaudRateOptions { get; } = [1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    [ObservableProperty]
    private int dataBits = 8;

    public int[] DataBitsOptions { get; } = [7, 8];

    [ObservableProperty]
    private Parity selectedParity = Parity.None;

    public Parity[] ParityOptions { get; } = [Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space];

    [ObservableProperty]
    private StopBits selectedStopBits = StopBits.One;

    public StopBits[] StopBitsOptions { get; } = [StopBits.One, StopBits.Two, StopBits.OnePointFive];

    [ObservableProperty]
    private bool dtrEnable = true;

    [ObservableProperty]
    private bool rtsEnable = true;

    // ====== TCP Client 参数 ======
    [ObservableProperty]
    private string tcpHost = "127.0.0.1";

    [ObservableProperty]
    private int tcpPort = 502;

    [ObservableProperty]
    private int connectTimeoutMs = 3000;

    [ObservableProperty]
    private bool autoReconnect = false;

    // ====== UDP 参数 ======
    [ObservableProperty]
    private string udpRemoteHost = "127.0.0.1";

    [ObservableProperty]
    private int udpRemotePort = 502;

    [ObservableProperty]
    private int udpLocalPort = 0;

    public ConnectionViewModel(ICommunicationService communicationService, ISnackbarService snackbarService, HslModbusServerService? serverService = null)
    {
        _communicationService = communicationService ?? throw new ArgumentNullException(nameof(communicationService));
        _snackbarService = snackbarService ?? throw new ArgumentNullException(nameof(snackbarService));
        _serverService = serverService;

        Title = "通讯连接";

        RefreshSerialPorts();

        LoadDefaultServerConfiguration();
        _communicationService.StateChanged += (s, e) =>
        {
            IsConnected = _communicationService.IsConnected;
            ConnectionStateText = e.NewState switch
            {
                ChannelState.Connected => "已连接",
                ChannelState.Connecting => "连接中...",
                ChannelState.Reconnecting => "重连中...",
                ChannelState.Faulted => "连接异常",
                _ => "未连接"
            };
        };
    }

    [RelayCommand]
    public void StartServer()
    {
        try
        {
            if (_serverService is null) throw new InvalidOperationException("Modbus TCP 服务未配置");
            _serverService.ConfigureRegisters(ServerRegisters);
            _serverService.Start(ServerPort, ServerStation);
            IsServerRunning = _serverService.IsRunning;
            ServerStatusText = $"运行中：端口 {ServerPort}，站号 {ServerStation}，数据定义 {ServerRegisters.Count} 项";
            _snackbarService.Show("服务器已启动", $"Modbus TCP Server 监听端口 {ServerPort}", ControlAppearance.Success);
            SaveDefaultServerConfiguration();
        }
        catch (Exception ex)
        {
            IsServerRunning = _serverService?.IsRunning == true;
            ServerStatusText = $"启动失败：{ex.Message}";
            _snackbarService.Show("服务器启动失败", ex.Message, ControlAppearance.Error);
        }
    }

    [RelayCommand]
    public void StopServer()
    {
        _serverService?.Stop();
        IsServerRunning = false;
        ServerStatusText = "服务器已停止";
        _snackbarService.Show("服务器已停止", "Modbus TCP Server 已关闭", ControlAppearance.Info);
    }

    [RelayCommand]
    public void CreateServerRegister()
    {
        try
        {
            var definition = new ServerRegisterDefinition(SelectedRegisterArea, ServerRegisterAddress, SelectedRegisterDataType, SelectedRegisterByteOrder, ServerRegisterValue, ServerRegisterNote);
            ServerRegisterCodec.Validate(definition);
            var updated = ServerRegisters.Where(item => item.Area != definition.Area || item.Address != definition.Address).Append(definition).ToArray();
            ServerRegisterCodec.ValidateNoOverlap(updated);
            _serverService?.ConfigureRegisters(updated);
            ServerRegisters = new ObservableCollection<ServerRegisterDefinition>(updated.OrderBy(item => item.Area).ThenBy(item => item.Address));
            SaveDefaultServerConfiguration();
            ServerStatusText = $"已配置 {ServerRegisters.Count} 项数据定义";
            _snackbarService.Show("数据定义已保存", $"{definition.Area} 地址 {definition.Address} = {definition.InitialValue}", ControlAppearance.Success);
        }
        catch (Exception ex)
        {
            _snackbarService.Show("建立数据定义失败", ex.Message, ControlAppearance.Error);
        }
    }

    [RelayCommand]
    public void RemoveServerRegister(ServerRegisterDefinition? definition)
    {
        if (definition is null) return;
        try
        {
            var updated = ServerRegisters.Where(item => item != definition).ToArray();
            _serverService?.ConfigureRegisters(updated);
            ServerRegisters = new ObservableCollection<ServerRegisterDefinition>(updated);
            SelectedServerRegister = null;
            SaveDefaultServerConfiguration();
            ServerStatusText = $"已配置 {ServerRegisters.Count} 项数据定义";
        }
        catch (Exception ex)
        {
            _snackbarService.Show("删除数据定义失败", ex.Message, ControlAppearance.Error);
        }
    }

    [RelayCommand]
    public void ExportServerConfiguration()
    {
        var dialog = new SaveFileDialog { Filter = "JSON 配置文件 (*.json)|*.json", FileName = "modbus-server.json" };
        if (dialog.ShowDialog() == true) SaveServerConfiguration(dialog.FileName);
    }

    [RelayCommand]
    public void ImportServerConfiguration()
    {
        var dialog = new OpenFileDialog { Filter = "JSON 配置文件 (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog() == true) LoadServerConfiguration(dialog.FileName);
    }

    private void SaveDefaultServerConfiguration() => SaveServerConfiguration(ConfigurationPath);

    private void SaveServerConfiguration(string path)
    {
        try
        {
            var configuration = new ModbusServerConfiguration(1, ServerPort, ServerStation, ServerRegisters.ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(configuration, JsonOptions));
            ServerStatusText = $"配置已保存：{path}";
        }
        catch (Exception ex)
        {
            ServerStatusText = $"保存失败：{ex.Message}";
            _snackbarService.Show("配置保存失败", ex.Message, ControlAppearance.Error);
        }
    }

    private void LoadDefaultServerConfiguration()
    {
        if (File.Exists(ConfigurationPath)) LoadServerConfiguration(ConfigurationPath);
    }

    private void LoadServerConfiguration(string path)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<ModbusServerConfiguration>(File.ReadAllText(path))
                ?? throw new InvalidDataException("配置文件为空或格式不正确");
            if (configuration.Version != 1) throw new InvalidDataException($"不支持的配置版本：{configuration.Version}");
            if (configuration.Port is < 1 or > 65535) throw new InvalidDataException("服务器端口必须在 1 至 65535 之间");
            if (configuration.Station == 0) throw new InvalidDataException("站号不能为 0");
            foreach (var definition in configuration.Registers) ServerRegisterCodec.Validate(definition);
            ServerRegisterCodec.ValidateNoOverlap(configuration.Registers);
            _serverService?.ConfigureRegisters(configuration.Registers);
            ServerPort = configuration.Port;
            ServerStation = configuration.Station;
            ServerRegisters = new ObservableCollection<ServerRegisterDefinition>(configuration.Registers.OrderBy(item => item.Area).ThenBy(item => item.Address));
            ServerStatusText = $"已加载 {ServerRegisters.Count} 项数据定义";
        }
        catch (Exception ex)
        {
            ServerStatusText = $"配置加载失败：{ex.Message}";
            _snackbarService.Show("配置加载失败", ex.Message, ControlAppearance.Error);
        }
    }

    [RelayCommand]
    public void RefreshSerialPorts()
    {
        AvailablePorts.Clear();
        var ports = SerialPort.GetPortNames();
        foreach (var p in ports)
        {
            AvailablePorts.Add(p);
        }

        if (AvailablePorts.Count > 0)
        {
            if (string.IsNullOrEmpty(SelectedPort) || !AvailablePorts.Contains(SelectedPort))
            {
                SelectedPort = AvailablePorts[0];
            }
        }
        else
        {
            AvailablePorts.Add("COM1");
            SelectedPort = "COM1";
        }
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (IsConnected)
        {
            return;
        }

        IsBusy = true;
        try
        {
            bool success = false;
            switch (SelectedCommTypeIndex)
            {
                case 0: // 串口
                    var serialParams = new SerialPortParameters
                    {
                        PortName = SelectedPort,
                        BaudRate = SelectedBaudRate,
                        DataBits = DataBits,
                        Parity = SelectedParity,
                        StopBits = SelectedStopBits,
                        DtrEnable = DtrEnable,
                        RtsEnable = RtsEnable
                    };
                    success = await _communicationService.ConnectSerialAsync(serialParams);
                    break;

                case 1: // TCP Client
                    var tcpParams = new TcpClientParameters
                    {
                        Host = TcpHost,
                        Port = TcpPort,
                        ConnectTimeoutMs = ConnectTimeoutMs,
                        AutoReconnect = AutoReconnect
                    };
                    success = await _communicationService.ConnectTcpAsync(tcpParams);
                    break;

                case 2: // UDP
                    var udpParams = new UdpParameters
                    {
                        RemoteHost = UdpRemoteHost,
                        RemotePort = UdpRemotePort,
                        LocalPort = UdpLocalPort
                    };
                    success = await _communicationService.ConnectUdpAsync(udpParams);
                    break;
            }

            if (success)
            {
                _snackbarService.Show("连接成功", $"已成功连接到 {_communicationService.CurrentChannelInfo}", ControlAppearance.Success);
            }
        }
        catch (Exception ex)
        {
            _snackbarService.Show("连接失败", ex.Message, ControlAppearance.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await _communicationService.DisconnectAsync();
            _snackbarService.Show("连接已断开", "通讯通道已安全关闭", ControlAppearance.Info);
        }
        catch (Exception ex)
        {
            _snackbarService.Show("断开连接异常", ex.Message, ControlAppearance.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
