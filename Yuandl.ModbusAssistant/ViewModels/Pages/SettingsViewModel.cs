using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yuandl.ThemeUI.Enums;
using Yuandl.ThemeUI.Managers;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

/// <summary>
/// 系统设置页面 ViewModel
/// 负责主题切换、通讯默认超时重试、语言与日志策略配置
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private int selectedThemeIndex;

    [ObservableProperty]
    private int defaultTimeoutMs = 1000;

    [ObservableProperty]
    private int defaultRetryTimes = 2;

    public SettingsViewModel()
    {
        Title = "系统设置";
        var currentTheme = ApplicationThemeManager.GetAppTheme();
        selectedThemeIndex = currentTheme == ApplicationTheme.Light ? 0 : 1;
    }

    [RelayCommand]
    private void OnThemeChanged(int index)
    {
        SelectedThemeIndex = index;
        ApplicationThemeManager.Apply(index == 0 ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }
}
