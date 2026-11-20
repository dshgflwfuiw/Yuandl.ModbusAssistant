using System.Diagnostics;
using System.IO;
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
        _defaultTimeoutMs = Math.Clamp(saved.DefaultTimeoutMs, 100, 30000);
        _defaultRetryTimes = Math.Clamp(saved.DefaultRetryTimes, 0, 10);

        SaveCommand = new RelayCommand(SaveSettings);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
    }

    public event EventHandler<bool>? ThemeChanged;

    public string LogDirectory => AppLogger.LogDirectory;

    private bool _isDarkTheme;
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                OnPropertyChanged(nameof(ThemeMode));
                SaveSettings();
                ThemeChanged?.Invoke(this, value);
            }
        }
    }

    public string ThemeMode
    {
        get => IsDarkTheme ? "深色模式 (Dark)" : "浅色模式 (Light)";
        set
        {
            bool isDark = value == "深色模式 (Dark)";
            if (IsDarkTheme != isDark)
                IsDarkTheme = isDark;
        }
    }

    private int _defaultTimeoutMs = 1000;
    public int DefaultTimeoutMs
    {
        get => _defaultTimeoutMs;
        set
        {
            int validated = Math.Clamp(value, 100, 30000);
            if (SetProperty(ref _defaultTimeoutMs, validated))
                SaveSettings();
        }
    }

    private int _defaultRetryTimes = 2;
    public int DefaultRetryTimes
    {
        get => _defaultRetryTimes;
        set
        {
            if (SetProperty(ref _defaultRetryTimes, Math.Clamp(value, 0, 10)))
                SaveSettings();
        }
    }

    private string _saveBanner = string.Empty;
    public string SaveBanner { get => _saveBanner; private set => SetProperty(ref _saveBanner, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }

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

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLogger.LogDirectory);
            Process.Start(new ProcessStartInfo(AppLogger.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SaveBanner = $"无法打开日志目录: {ex.Message}";
        }
    }
}
