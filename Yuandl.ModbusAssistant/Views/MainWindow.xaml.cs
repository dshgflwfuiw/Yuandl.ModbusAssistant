using System;
using System.Linq;
using System.Windows;
using Yuandl.ModbusAssistant.ViewModels;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Services;

namespace Yuandl.ModbusAssistant.Views;

/// <summary>
/// 主窗体交互逻辑
/// 接入 Yuandl.ThemeUI 导航中心、全局弹窗宿主与浮动通知宿主
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _serviceProvider;
    private readonly INavigationService _navigationService;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        IServiceProvider serviceProvider,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;

        _serviceProvider = serviceProvider;
        _navigationService = navigationService;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;

        InitializeComponent();

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 挂载导航控制器与服务提供者
        _navigationService.SetNavigationControl(RootNavigation);
        RootNavigation.SetServiceProvider(_serviceProvider);

        // 挂载 Snackbar 与 ContentDialog 宿主
        _snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        _contentDialogService.SetDialogHost(RootContentDialog);

        // 默认导航至第一项（通讯连接页）
        if (ViewModel.NavigationItems.Count > 0)
        {
            _ = RootNavigation.Navigate(ViewModel.NavigationItems.First());
        }
    }
}
