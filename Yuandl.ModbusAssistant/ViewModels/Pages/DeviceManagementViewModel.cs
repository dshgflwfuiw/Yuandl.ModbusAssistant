using System.Collections.ObjectModel;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class DeviceManagementViewModel : ObservableObject
{
    private readonly PersistenceService _persistence;
    private List<DeviceProfile> _savedSnapshot = new();

    public DeviceManagementViewModel(PersistenceService persistence)
    {
        _persistence = persistence;
        var loaded = _persistence.LoadProfiles();
        foreach (var profile in loaded)
            Profiles.Add(CloneProfile(profile));
        _savedSnapshot = loaded.Select(CloneProfile).ToList();

        AddProfileCommand = new RelayCommand(AddProfile);
        LoadProfileCommand = new AsyncRelayCommand(p => ApplyProfileAsync(p, autoConnect: false));
        LoadAndConnectCommand = new AsyncRelayCommand(p => ApplyProfileAsync(p, autoConnect: true));
        DeleteProfileCommand = new RelayCommand(p =>
        {
            if (p is DeviceProfile profile)
            {
                Profiles.Remove(profile);
                HasUnsavedChanges = true;
                StatusBanner = $"已移除方案: {profile.Name}（请点击“保存变更”落盘或“撤销修改”还原）";
            }
        });
        SaveProfilesCommand = new RelayCommand(SaveProfiles, () => HasUnsavedChanges);
        RevertProfilesCommand = new RelayCommand(RevertProfiles, () => HasUnsavedChanges);
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

    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                SaveProfilesCommand?.RaiseCanExecuteChanged();
                RevertProfilesCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _statusBanner = string.Empty;
    public string StatusBanner { get => _statusBanner; private set => SetProperty(ref _statusBanner, value); }

    public RelayCommand AddProfileCommand { get; }
    public AsyncRelayCommand LoadProfileCommand { get; }
    public AsyncRelayCommand LoadAndConnectCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand SaveProfilesCommand { get; }
    public RelayCommand RevertProfilesCommand { get; }

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
        HasUnsavedChanges = true;
        Name = string.Empty;
        StatusBanner = $"已暂存设备方案: {profile.Name}（点击“保存变更”生效）";
    }

    private void SaveProfiles()
    {
        _persistence.SaveProfiles(Profiles);
        _savedSnapshot = Profiles.Select(CloneProfile).ToList();
        HasUnsavedChanges = false;
        StatusBanner = $"已保存 {Profiles.Count} 条设备方案到本地配置";
    }

    private void RevertProfiles()
    {
        Profiles.Clear();
        foreach (var item in _savedSnapshot)
            Profiles.Add(CloneProfile(item));
        HasUnsavedChanges = false;
        StatusBanner = "已撤销未保存的设备方案修改";
    }

    private async Task ApplyProfileAsync(object? parameter, bool autoConnect)
    {
        if (parameter is not DeviceProfile profile) return;
        if (OnApplyProfile == null) return;

        StatusBanner = autoConnect
            ? $"正在载入并连接 {profile.Name}..."
            : $"已载入参数方案 {profile.Name}，已切换至「通讯连接」页";
        await OnApplyProfile(profile, autoConnect);
    }

    private static DeviceProfile CloneProfile(DeviceProfile source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Protocol = source.Protocol,
        Endpoint = source.Endpoint,
        StationId = source.StationId
    };
}
