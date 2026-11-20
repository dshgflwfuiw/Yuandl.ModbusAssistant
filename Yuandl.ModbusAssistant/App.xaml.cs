using System.Windows;
using System.Windows.Threading;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ModbusAssistant.ViewModels;
using Yuandl.ModbusAssistant.Views;

namespace Yuandl.ModbusAssistant;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLogger.Info("Yuandl.ModbusAssistant 启动");

        _mainViewModel = new MainViewModel();
        ApplyTheme(_mainViewModel.Settings.IsDarkTheme);
        _mainViewModel.Settings.ThemeChanged += (_, isDark) => ApplyTheme(isDark);

        var window = new MainWindow
        {
            DataContext = _mainViewModel
        };
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Fatal("UI 线程未处理异常", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"程序遇到未预期错误，但已尝试安全拦截：\n{e.Exception.Message}\n\n详细日志已保存至：\n{AppLogger.LogDirectory}",
            "Yuandl.ModbusAssistant 异常提示",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLogger.Fatal($"应用程序域未处理异常 (IsTerminating={e.IsTerminating})", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("后台异步任务未观察异常", e.Exception);
        e.SetObserved();
    }

    public void ApplyTheme(bool isDark)
    {
        string themeFile = isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
        var source = new Uri($"pack://application:,,,/Yuandl.ModbusAssistant;component/{themeFile}", UriKind.Absolute);
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("Yuandl.ModbusAssistant 正常退出");
        _mainViewModel?.Dispose();
        base.OnExit(e);
    }
}
