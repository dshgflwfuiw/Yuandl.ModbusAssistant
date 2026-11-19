using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ModbusAssistant.ViewModels;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ModbusAssistant.Views;
using Yuandl.ModbusAssistant.Views.Pages;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Services;

namespace Yuandl.ModbusAssistant;

/// <summary>
/// 应用程序主入口，负责依赖注入容器构建、生命周期管理与全局防御性异常捕获
/// </summary>
public partial class App : Application
{
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices((context, services) =>
        {
            // 1. Yuandl.ThemeUI 核心交互服务
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();
            services.AddSingleton<IThemeService, ThemeService>();

            // 2. 通讯基础设施与服务
            services.AddSingleton<IChannelFactory, ChannelFactory>();
            services.AddSingleton<ICommunicationService, CommunicationService>();
            services.AddSingleton<HslModbusService>();
            services.AddSingleton<HslModbusServerService>();

            // 3. 托管生命周期服务
            services.AddHostedService<ApplicationHostService>();

            // 3. 主窗口及主 ViewModel
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();

            // 4. 各模块 ViewModels (导航单例以保持会话状态)
            services.AddSingleton<ConnectionViewModel>();
            services.AddSingleton<ModbusDebugViewModel>();
            services.AddSingleton<RawDebugViewModel>();
            services.AddSingleton<RegisterMonitorViewModel>();
            services.AddSingleton<PacketAnalysisViewModel>();
            services.AddSingleton<DeviceManagementViewModel>();
            services.AddSingleton<SettingsViewModel>();

            // 5. 各模块 View Pages
            services.AddTransient<ConnectionPage>();
            services.AddTransient<ModbusDebugPage>();
            services.AddTransient<RawDebugPage>();
            services.AddTransient<RegisterMonitorPage>();
            services.AddTransient<PacketAnalysisPage>();
            services.AddTransient<DeviceManagementPage>();
            services.AddTransient<SettingsPage>();
        })
        .Build();

    public static T GetService<T>() where T : class
    {
        return _host.Services.GetRequiredService<T>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局防御性异常捕获，确保工业调试中突发通讯异常不导致程序闪退崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        await _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[UI异常] {e.Exception}");
        // 提示用户或记录日志，避免UI线程静默闪退
        System.Windows.MessageBox.Show($"发生未处理的界面异常: {e.Exception.Message}", "系统警告", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Debug.WriteLine($"[域异常] {ex}");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Debug.WriteLine($"[异步任务异常] {e.Exception}");
        e.SetObserved();
    }
}
