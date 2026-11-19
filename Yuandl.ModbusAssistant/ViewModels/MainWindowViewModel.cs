using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ModbusAssistant.Views.Pages;
using Yuandl.ThemeUI.Appearance;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Enums;
using Yuandl.ThemeUI.Managers;

namespace Yuandl.ModbusAssistant.ViewModels;

/// <summary>
/// 主窗体 ViewModel
/// 统一管理全局侧边栏导航、沉浸式状态栏与主题切换，实时响应底层通讯状态
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICommunicationService _communicationService;

    [ObservableProperty]
    private string windowTitle = "Yuandl Modbus Assistant";

    [ObservableProperty]
    private ObservableCollection<PageViewItem> navigationItems = [];

    // ====== 底部状态栏工业通讯监视属性 ======

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string connectionStateText = "未连接";

    [ObservableProperty]
    private string communicationType = "无活动连接";

    [ObservableProperty]
    private long txBytes = 0;

    [ObservableProperty]
    private long rxBytes = 0;

    [ObservableProperty]
    private int errorCount = 0;

    [ObservableProperty]
    private string deviceInfo = "就绪";

    public MainWindowViewModel(ICommunicationService communicationService)
    {
        _communicationService = communicationService ?? throw new ArgumentNullException(nameof(communicationService));

        InitializeNavigation();
        SubscribeCommunicationEvents();
    }

    private void InitializeNavigation()
    {
        NavigationItems =
        [
            new PageViewItem
            {
                Content = "通讯连接",
                ToolTip = "配置串口与网络通讯链路",
                Icon = new SymbolIcon { Symbol = SymbolRegular.PlugConnected24 },
                PageType = typeof(ConnectionPage),
                CacheMode = NavigationCacheMode.Enabled
            },
            new PageViewItem
            {
                Content = "Modbus 调试",
                ToolTip = "标准功能码读写与异常分析",
                Icon = new SymbolIcon { Symbol = SymbolRegular.DeveloperBoard24 },
                PageType = typeof(ModbusDebugPage),
                CacheMode = NavigationCacheMode.Enabled
            },
            new PageViewItem
            {
                Content = "无协议调试",
                ToolTip = "纯净原始字节流/HEX/ASCII收发",
                Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24 },
                PageType = typeof(RawDebugPage),
                CacheMode = NavigationCacheMode.Enabled
            },
            new PageViewItem
            {
                Content = "寄存器监视",
                ToolTip = "高频连续寄存器轮询与格式转换",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Gauge24 },
                PageType = typeof(RegisterMonitorPage),
                CacheMode = NavigationCacheMode.Enabled
            },
            new PageViewItem
            {
                Content = "报文分析",
                ToolTip = "请求响应匹配与时延统计",
                Icon = new SymbolIcon { Symbol = SymbolRegular.DocumentSearch24 },
                PageType = typeof(PacketAnalysisPage),
                CacheMode = NavigationCacheMode.Enabled
            },
            new PageViewItem
            {
                Content = "设备管理",
                ToolTip = "设备配置方案导入导出",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Desktop24 },
                PageType = typeof(DeviceManagementPage),
                CacheMode = NavigationCacheMode.Enabled
            },
            new PageViewItem
            {
                Content = "系统设置",
                ToolTip = "外观风格与通讯参数设定",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                PageType = typeof(SettingsPage),
                CacheMode = NavigationCacheMode.Enabled
            }
        ];
    }

    private void SubscribeCommunicationEvents()
    {
        _communicationService.StateChanged += OnChannelStateChanged;
        _communicationService.DataReceived += OnChannelDataReceived;
        _communicationService.ErrorOccurred += OnChannelErrorOccurred;
    }

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsConnected = _communicationService.IsConnected;
            ConnectionStateText = e.NewState switch
            {
                ChannelState.Connected => "已连接",
                ChannelState.Connecting => "正在连接...",
                ChannelState.Reconnecting => "正在重连...",
                ChannelState.Faulted => "连接异常",
                _ => "未连接"
            };

            CommunicationType = _communicationService.CurrentChannelInfo;
            UpdateStatistics();
        });
    }

    private void OnChannelDataReceived(object? sender, DataReceivedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(UpdateStatistics);
    }

    private void OnChannelErrorOccurred(object? sender, Exception e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            UpdateStatistics();
            DeviceInfo = $"错误: {e.Message}";
        });
    }

    public void UpdateStatistics()
    {
        var stats = _communicationService.Statistics;
        TxBytes = stats.BytesSent;
        RxBytes = stats.BytesReceived;
        ErrorCount = (int)stats.ErrorsCount;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var currentTheme = ApplicationThemeManager.GetAppTheme();
        var newTheme = currentTheme == ApplicationTheme.Light ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(newTheme);
    }
}
