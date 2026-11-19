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
        DeleteProfileCommand = new RelayCommand(p =>
        {
            if (p is DeviceProfile profile)
            {
                Profiles.Remove(profile);
                _persistence.SaveProfiles(Profiles);
            }
        });
    }

    public ObservableCollection<DeviceProfile> Profiles { get; } = new();
    public IReadOnlyList<string> Protocols { get; } = new[] { "Modbus TCP", "Modbus RTU", "Modbus ASCII", "TCP Server", "UDP" };

    private string _name = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _protocol = "Modbus TCP";
    public string Protocol { get => _protocol; set => SetProperty(ref _protocol, value); }

    private string _endpoint = "127.0.0.1:5020";
    public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }

    private int _stationId = 1;
    public int StationId { get => _stationId; set => SetProperty(ref _stationId, value); }

    public RelayCommand AddProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }

    private void AddProfile()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Endpoint))
            return;

        Profiles.Add(new DeviceProfile
        {
            Name = Name.Trim(),
            Protocol = Protocol,
            Endpoint = Endpoint.Trim(),
            StationId = Math.Clamp(StationId, 1, 247)
        });
        _persistence.SaveProfiles(Profiles);
        Name = string.Empty;
    }
}
