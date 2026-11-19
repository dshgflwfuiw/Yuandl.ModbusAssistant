using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Hosting;
using Yuandl.ModbusAssistant.Views;

namespace Yuandl.ModbusAssistant.Services;

/// <summary>
/// 应用程序托管生命周期服务
/// 负责在宿主启动时创建并激活主窗体
/// </summary>
public class ApplicationHostService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICommunicationService _communicationService;
    private readonly HslModbusServerService _serverService;

    public ApplicationHostService(IServiceProvider serviceProvider, ICommunicationService communicationService, HslModbusServerService serverService)
    {
        _serviceProvider = serviceProvider;
        _communicationService = communicationService;
        _serverService = serverService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 确保在 WPF UI 线程上激活主窗体
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!Application.Current.Windows.OfType<MainWindow>().Any())
            {
                var mainWindow = _serviceProvider.GetService(typeof(MainWindow)) as MainWindow;
                mainWindow?.Show();
            }
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _serverService.Stop();
        await _communicationService.DisconnectAsync().ConfigureAwait(false);
    }
}
