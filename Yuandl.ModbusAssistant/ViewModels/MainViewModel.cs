using System.Windows;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ModbusAssistant.ViewModels.Pages;

namespace Yuandl.ModbusAssistant.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly PersistenceService _persistence;
    public CommunicationService CommunicationService { get; }
    public ModbusServerService ModbusServerService { get; }

    public ConnectionViewModel Connection { get; }
    public ModbusDebugViewModel ModbusDebug { get; }
    public RawDebugViewModel RawDebug { get; }
    public RegisterMonitorViewModel RegisterMonitor { get; }
    public PacketAnalysisViewModel PacketAnalysis { get; }
    public DeviceManagementViewModel DeviceManagement { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel()
    {
        _persistence = new PersistenceService();
        CommunicationService = new CommunicationService();
        ModbusServerService = new ModbusServerService();
        Settings = new SettingsViewModel(_persistence);

        Connection = new ConnectionViewModel(CommunicationService, ModbusServerService, _persistence);
        ModbusDebug = new ModbusDebugViewModel(CommunicationService, Settings);
        RawDebug = new RawDebugViewModel(CommunicationService);
        RegisterMonitor = new RegisterMonitorViewModel(CommunicationService, ModbusServerService, Settings, _persistence);
        PacketAnalysis = new PacketAnalysisViewModel(CommunicationService);
        DeviceManagement = new DeviceManagementViewModel(_persistence);

        CommunicationService.StateChanged += (_, _) => RunOnUi(UpdateStatusBar);
        CommunicationService.TrafficCaptured += (_, _) => RunOnUi(UpdateStatusBar);
        CommunicationService.ErrorOccurred += (_, msg) => RunOnUi(() => LastAlert = msg);
        ModbusServerService.StatusChanged += (_, _) => RunOnUi(UpdateStatusBar);

        UpdateStatusBar();
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }

    private string _statusSummary = "未连接";
    public string StatusSummary { get => _statusSummary; private set => SetProperty(ref _statusSummary, value); }

    private string _trafficSummary = "TX: 0 B | RX: 0 B";
    public string TrafficSummary { get => _trafficSummary; private set => SetProperty(ref _trafficSummary, value); }

    private string _serverBadgeText = "Modbus 服务器: 已停止";
    public string ServerBadgeText { get => _serverBadgeText; private set => SetProperty(ref _serverBadgeText, value); }

    private string _lastAlert = string.Empty;
    public string LastAlert { get => _lastAlert; private set => SetProperty(ref _lastAlert, value); }

    private void UpdateStatusBar()
    {
        var state = CommunicationService.State;
        var type = CommunicationService.ActiveType?.ToString() ?? "无通道";
        StatusSummary = state == ChannelState.Connected ? $"已连接 ({type})" : $"状态: {state}";

        var stats = CommunicationService.Statistics;
        TrafficSummary = $"TX: {stats.BytesSent} B ({stats.PacketsSent}帧) | RX: {stats.BytesReceived} B ({stats.PacketsReceived}帧) | ERR: {stats.ErrorsCount}";
        ServerBadgeText = $"Modbus 服务器: {ModbusServerService.StatusText}";
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        RegisterMonitor.StopMonitoring();
        CommunicationService.Dispose();
        ModbusServerService.Dispose();
    }
}
