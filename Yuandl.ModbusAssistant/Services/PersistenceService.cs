using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yuandl.ModbusAssistant.Models;

namespace Yuandl.ModbusAssistant.Services;

public sealed class PersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _baseDirectory;
    private string SettingsPath => Path.Combine(_baseDirectory, "settings.json");
    private string ProfilesPath => Path.Combine(_baseDirectory, "profiles.json");
    private string ServerConfigPath => Path.Combine(_baseDirectory, "server-config.json");
    private string MonitorConfigPath => Path.Combine(_baseDirectory, "monitor-config.json");

    public PersistenceService()
    {
        _baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yuandl.ModbusAssistant");
        Directory.CreateDirectory(_baseDirectory);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch { /* fallback to defaults */ }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch { /* ignore IO failure */ }
    }

    public List<DeviceProfile> LoadProfiles()
    {
        try
        {
            if (File.Exists(ProfilesPath))
            {
                var json = File.ReadAllText(ProfilesPath);
                var list = JsonSerializer.Deserialize<List<DeviceProfile>>(json, JsonOptions);
                if (list != null) return list;
            }
        }
        catch { /* fallback */ }

        return new List<DeviceProfile>
        {
            new() { Name = "本地仿真站", Protocol = "Modbus TCP", Endpoint = "127.0.0.1:5020", StationId = 1 },
            new() { Name = "温湿度传感器", Protocol = "Modbus RTU", Endpoint = "COM3 @ 9600", StationId = 2 }
        };
    }

    public void SaveProfiles(IEnumerable<DeviceProfile> profiles)
    {
        try
        {
            File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(profiles.ToList(), JsonOptions));
        }
        catch { /* ignore IO failure */ }
    }

    public ModbusServerConfiguration LoadServerConfig()
    {
        try
        {
            if (File.Exists(ServerConfigPath))
            {
                var json = File.ReadAllText(ServerConfigPath);
                var cfg = JsonSerializer.Deserialize<ModbusServerConfiguration>(json, JsonOptions);
                if (cfg != null) return cfg;
            }
        }
        catch { /* fallback */ }

        return new ModbusServerConfiguration
        {
            Port = 5020,
            Station = 1,
            Registers = new List<ServerRegisterDefinition>
            {
                new() { Area = Core.Enums.ServerRegisterArea.HoldingRegister, Address = 0, DataType = Core.Enums.ServerRegisterDataType.UInt16, InitialValue = "100", CurrentValue = "100", Note = "温度设定" },
                new() { Area = Core.Enums.ServerRegisterArea.HoldingRegister, Address = 1, DataType = Core.Enums.ServerRegisterDataType.Int16, InitialValue = "-25", CurrentValue = "-25", Note = "偏移量" },
                new() { Area = Core.Enums.ServerRegisterArea.Coil, Address = 0, DataType = Core.Enums.ServerRegisterDataType.Bit, InitialValue = "true", CurrentValue = "true", Note = "运行使能" }
            }
        };
    }

    public void SaveServerConfig(ModbusServerConfiguration config)
    {
        try
        {
            File.WriteAllText(ServerConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch { /* ignore IO failure */ }
    }

    public MonitorConfiguration LoadMonitorConfiguration()
    {
        try
        {
            if (File.Exists(MonitorConfigPath))
            {
                var json = File.ReadAllText(MonitorConfigPath);
                return JsonSerializer.Deserialize<MonitorConfiguration>(json, JsonOptions) ?? new MonitorConfiguration();
            }
        }
        catch { /* fallback to empty monitor configuration */ }

        return new MonitorConfiguration();
    }

    public void SaveMonitorConfiguration(MonitorConfiguration config)
    {
        try
        {
            File.WriteAllText(MonitorConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch { /* ignore IO failure */ }
    }

    public static string SerializeServerConfig(ModbusServerConfiguration config) =>
        JsonSerializer.Serialize(config, JsonOptions);

    public static ModbusServerConfiguration? DeserializeServerConfig(string json) =>
        JsonSerializer.Deserialize<ModbusServerConfiguration>(json, JsonOptions);
}
