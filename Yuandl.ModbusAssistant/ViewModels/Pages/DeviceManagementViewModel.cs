using System.Collections.ObjectModel;
using System.Text.Json;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public partial class DeviceManagementViewModel : ViewModelBase
{
    public sealed record DeviceProfile(string Name, string Protocol, string Endpoint, byte StationId);
    [ObservableProperty] private ObservableCollection<DeviceProfile> profiles = [];
    [ObservableProperty] private string currentProfileName = "默认设备方案";
    [ObservableProperty] private string newProfileName = "新设备";
    [ObservableProperty] private string newProtocol = "Modbus TCP";
    [ObservableProperty] private string newEndpoint = "127.0.0.1:502";
    [ObservableProperty] private byte newStationId = 1;
    [ObservableProperty] private string statusText = "就绪";
    private string ProfilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yuandl.ModbusAssistant", "devices.json");

    public DeviceManagementViewModel() { Title = "设备管理"; Load(); }

    [RelayCommand] public void AddProfile() { Profiles.Add(new DeviceProfile(NewProfileName, NewProtocol, NewEndpoint, NewStationId)); Save(); StatusText = "设备配置已添加"; }
    [RelayCommand] public void RemoveProfile(DeviceProfile? profile) { if (profile != null) Profiles.Remove(profile); Save(); }
    [RelayCommand] public void SaveProfiles() { Save(); StatusText = $"已保存 {Profiles.Count} 个设备配置"; }
    [RelayCommand] public void LoadProfiles() { Load(); StatusText = $"已加载 {Profiles.Count} 个设备配置"; }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(Profiles, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Load()
    {
        try { Profiles.Clear(); if (File.Exists(ProfilePath)) foreach (var profile in JsonSerializer.Deserialize<List<DeviceProfile>>(File.ReadAllText(ProfilePath)) ?? []) Profiles.Add(profile); }
        catch (Exception ex) { StatusText = $"加载失败：{ex.Message}"; }
    }
}
