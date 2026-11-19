using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ModbusAssistant.ViewModels;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ModbusAssistant.Views;
using Yuandl.ModbusAssistant.Views.Pages;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Services;

namespace Yuandl.ModbusAssistant.Tests;

[TestClass]
public class NavigationAndDiTests
{
    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new AggregateException("STA thread execution failed", exception);
        }
    }

    [TestMethod]
    public void MainWindowViewModel_ShouldInitialize_AllSevenNavigationItems()
    {
        RunInSta(() =>
        {
            // Arrange & Act
            var commService = new CommunicationService(new ChannelFactory());
            var vm = new MainWindowViewModel(commService);

            // Assert
            Assert.AreEqual(7, vm.NavigationItems.Count, "应该包含 7 个功能导航菜单项");
            Assert.AreEqual("通讯连接", vm.NavigationItems[0].Content);
            Assert.AreEqual(typeof(ConnectionPage), vm.NavigationItems[0].PageType);

            Assert.AreEqual("Modbus 调试", vm.NavigationItems[1].Content);
            Assert.AreEqual(typeof(ModbusDebugPage), vm.NavigationItems[1].PageType);

            Assert.AreEqual("无协议调试", vm.NavigationItems[2].Content);
            Assert.AreEqual(typeof(RawDebugPage), vm.NavigationItems[2].PageType);

            Assert.AreEqual("寄存器监视", vm.NavigationItems[3].Content);
            Assert.AreEqual(typeof(RegisterMonitorPage), vm.NavigationItems[3].PageType);

            Assert.AreEqual("报文分析", vm.NavigationItems[4].Content);
            Assert.AreEqual(typeof(PacketAnalysisPage), vm.NavigationItems[4].PageType);

            Assert.AreEqual("设备管理", vm.NavigationItems[5].Content);
            Assert.AreEqual(typeof(DeviceManagementPage), vm.NavigationItems[5].PageType);

            Assert.AreEqual("系统设置", vm.NavigationItems[6].Content);
            Assert.AreEqual(typeof(SettingsPage), vm.NavigationItems[6].PageType);
        });
    }

    [TestMethod]
    public void ServiceCollection_ShouldResolve_AllViewModels()
    {
        RunInSta(() =>
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();
            services.AddSingleton<IThemeService, ThemeService>();

            services.AddSingleton<IChannelFactory, ChannelFactory>();
            services.AddSingleton<ICommunicationService, CommunicationService>();

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();

            services.AddSingleton<ConnectionViewModel>();
            services.AddSingleton<ModbusDebugViewModel>();
            services.AddSingleton<RawDebugViewModel>();
            services.AddSingleton<RegisterMonitorViewModel>();
            services.AddSingleton<PacketAnalysisViewModel>();
            services.AddSingleton<DeviceManagementViewModel>();
            services.AddSingleton<SettingsViewModel>();

            services.AddTransient<ConnectionPage>();
            services.AddTransient<ModbusDebugPage>();
            services.AddTransient<RawDebugPage>();
            services.AddTransient<RegisterMonitorPage>();
            services.AddTransient<PacketAnalysisPage>();
            services.AddTransient<DeviceManagementPage>();
            services.AddTransient<SettingsPage>();

            var provider = services.BuildServiceProvider();

            // Act & Assert
            Assert.IsNotNull(provider.GetRequiredService<MainWindowViewModel>());
            Assert.IsNotNull(provider.GetRequiredService<ConnectionViewModel>());
            Assert.IsNotNull(provider.GetRequiredService<ModbusDebugViewModel>());
            Assert.IsNotNull(provider.GetRequiredService<RawDebugViewModel>());
            Assert.IsNotNull(provider.GetRequiredService<RegisterMonitorViewModel>());
            Assert.IsNotNull(provider.GetRequiredService<PacketAnalysisViewModel>());
            Assert.IsNotNull(provider.GetRequiredService<DeviceManagementViewModel>());
            Assert.IsNotNull(provider.GetRequiredService<SettingsViewModel>());
        });
    }
}
