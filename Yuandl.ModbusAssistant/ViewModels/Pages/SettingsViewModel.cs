using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly PersistenceService _persistence;

    public SettingsViewModel(PersistenceService persistence)
    {
        _persistence = persistence;
        var saved = _persistence.LoadSettings();
        _isDarkTheme = saved.IsDarkTheme;
        _defaultTimeoutMs = saved.DefaultTimeoutMs;
        _defaultRetryTimes = saved.DefaultRetryTimes;

        SaveCommand = new RelayCommand(SaveSettings);
    }

    public event EventHandler<bool>? ThemeChanged;

    private bool _isDarkTheme;
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                SaveSettings();
                ThemeChanged?.Invoke(this, value);
            }
        }
    }

    private int _defaultTimeoutMs = 1000;
    public int DefaultTimeoutMs { get => _defaultTimeoutMs; set => SetProperty(ref _defaultTimeoutMs, value); }

    private int _defaultRetryTimes = 2;
    public int DefaultRetryTimes { get => _defaultRetryTimes; set => SetProperty(ref _defaultRetryTimes, value); }

    private string _saveBanner = string.Empty;
    public string SaveBanner { get => _saveBanner; private set => SetProperty(ref _saveBanner, value); }

    public RelayCommand SaveCommand { get; }

    private void SaveSettings()
    {
        _persistence.SaveSettings(new AppSettings
        {
            IsDarkTheme = IsDarkTheme,
            DefaultTimeoutMs = DefaultTimeoutMs,
            DefaultRetryTimes = DefaultRetryTimes
        });
        SaveBanner = $"系统设置已保存 ({DateTime.Now:HH:mm:ss})";
    }
}
