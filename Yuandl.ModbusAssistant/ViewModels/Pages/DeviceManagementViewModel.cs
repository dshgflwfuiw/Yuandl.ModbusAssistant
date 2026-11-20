using System.Collections.ObjectModel;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class DeviceManagementViewModel : ObservableObject
{
    private readonly PersistenceService _persistence;

    public DeviceManagementViewModel(PersistenceService persistence)
    {
        _persistence = persistence;
        foreach (var profile in _persistence.LoadProfiles())
            Profiles.Add(profile);

        AddProfileCommand = new RelayCommand(AddProfile);
        LoadProfileCommand = new RelayCommand(p => ApplyProfile(p, autoConnect: false));
        LoadAndConnectCommand = new RelayCommand(p => ApplyProfile(p, autoConnect: true));
        DeleteProfileCommand = new RelayCommand(p =>
        {
            if (p is DeviceProfile profile)
            {
                Profiles.Remove(profile);
                _persistence.SaveProfiles(Profiles);
                StatusBanner = $"已删除设备方案: {profile.Name}";
            }
        });
    }

    public Func<DeviceProfile, bool, Task>? OnApplyProfile { get; set; }

    public ObservableCollection<DeviceProfile> Profiles { get; } = new();
    public IReadOnlyList<string> Protocols { get; } = new[] { "Modbus TCP", "Modbus RTU", "Modbus ASCII", "TCP Server", "UDP" };

    private string _name = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _protocol = "Modbus TCP";
    public string Protocol { get => _protocol; set => SetProperty(ref _protocol, value); }

    private string _endpoint = "127.0.0.1:5020";
    public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }

    private int _stationId = 1;
    public int StationId { get => _stationId; set => SetProperty(ref _stationId, Math.Clamp(value, 1, 247)); }

    private string _statusBanner = string.Empty;
    public string StatusBanner { get => _statusBanner; private set => SetProperty(ref _statusBanner, value); }

    public RelayCommand AddProfileCommand { get; }
    public RelayCommand LoadProfileCommand { get; }
    public RelayCommand LoadAndConnectCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }

    private void AddProfile()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusBanner = "请输入设备名称";
            return;
        }
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            StatusBanner = "请输入通讯端点 (如 127.0.0.1:5020 或 COM3,9600)";
            return;
        }

        var profile = new DeviceProfile
        {
            Name = Name.Trim(),
            Protocol = Protocol,
            Endpoint = Endpoint.Trim(),
            StationId = Math.Clamp(StationId, 1, 247)
        };
        Profiles.Add(profile);
        _persistence.SaveProfiles(Profiles);
        Name = string.Empty;
        StatusBanner = $"已保存设备方案: {profile.Name}";
    }

    private void ApplyProfile(object? parameter, bool autoConnect)
    {
        if (parameter is not DeviceProfile profile) return;
        if (OnApplyProfile == null) return;

        StatusBanner = autoConnect
            ? $"正在载入并连接 {profile.Name}..."
            : $"已载入参数方案 {profile.Name}，已切换至「通讯连接」页";
        _ = OnApplyProfile(profile, autoConnect);
    }
}
