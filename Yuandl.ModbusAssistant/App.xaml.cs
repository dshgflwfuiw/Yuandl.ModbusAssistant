using System.Windows;
using Yuandl.ModbusAssistant.ViewModels;
using Yuandl.ModbusAssistant.Views;

namespace Yuandl.ModbusAssistant;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

    public void ApplyTheme(bool isDark)
    {
        string themeFile = isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
        var source = new Uri($"pack://application:,,,/Yuandl.ModbusAssistant;component/{themeFile}", UriKind.Absolute);
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        base.OnExit(e);
    }
}
