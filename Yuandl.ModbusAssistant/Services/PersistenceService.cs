using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yuandl.ModbusAssistant.Models;

namespace Yuandl.ModbusAssistant.Services;

public sealed class PersistenceService
{
    private static readonly JsonSerializerOptions PortableJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _baseDirectory;
    private readonly object _syncLock = new();

    private string WorkspaceConfigPath => Path.Combine(_baseDirectory, "workspace-config.json");
    private string SettingsPath => Path.Combine(_baseDirectory, "settings.json");
    private string ProfilesPath => Path.Combine(_baseDirectory, "profiles.json");
    private string ServerConfigPath => Path.Combine(_baseDirectory, "server-config.json");
    private string MonitorConfigPath => Path.Combine(_baseDirectory, "monitor-config.json");

    public PersistenceService(string? customDirectory = null)
    {
        _baseDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yuandl.ModbusAssistant");
        Directory.CreateDirectory(_baseDirectory);
    }

    public WorkspaceConfiguration LoadWorkspace()
    {
        lock (_syncLock)
        {
            if (File.Exists(WorkspaceConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(WorkspaceConfigPath, Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<WorkspaceConfiguration>(json, JsonOptions);
                    if (loaded != null)
                        return MigrateWorkspace(loaded);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"读取工作区配置文件失败 ({WorkspaceConfigPath})", ex);
                }
            }

            // Migrate from legacy v1 split JSON files if present
            var migrated = new WorkspaceConfiguration
            {
                SchemaVersion = WorkspaceConfiguration.CurrentSchemaVersion,
                Settings = LoadLegacySettings(),
                Profiles = LoadLegacyProfiles(),
                ServerConfig = LoadLegacyServerConfig(),
                MonitorConfig = LoadLegacyMonitorConfiguration()
            };

            if (File.Exists(SettingsPath) || File.Exists(ProfilesPath) || File.Exists(ServerConfigPath) || File.Exists(MonitorConfigPath))
            {
                AppLogger.Info("检测到旧版分立配置文件，已自动迁移至版本化 workspace-config.json (v2)");
                SaveWorkspace(migrated);
            }

            return migrated;
        }
    }

    public void SaveWorkspace(WorkspaceConfiguration workspace)
    {
        lock (_syncLock)
        {
            workspace.SchemaVersion = WorkspaceConfiguration.CurrentSchemaVersion;
            workspace.ServerConfig = MigrateServerConfig(workspace.ServerConfig);
            WriteFileAtomic(WorkspaceConfigPath, JsonSerializer.Serialize(workspace, JsonOptions));
        }
    }

    public static string SerializePortableWorkspace(WorkspaceConfiguration workspace)
    {
        var portable = new PortableWorkspaceFile
        {
            Settings = workspace.Settings,
            Profiles = workspace.Profiles,
            ServerConfig = workspace.ServerConfig,
            MonitorConfig = workspace.MonitorConfig
        };
        return JsonSerializer.Serialize(portable, PortableJsonOptions);
    }

    public static WorkspaceConfiguration DeserializePortableWorkspace(string json)
    {
        var portable = JsonSerializer.Deserialize<PortableWorkspaceFile>(json, PortableJsonOptions)
            ?? throw new InvalidDataException("工作区文件格式无效");
        if (!string.Equals(portable.Format, "yuandl-modbus-workspace", StringComparison.OrdinalIgnoreCase) || portable.Version != 1)
            throw new InvalidDataException("不支持的工作区格式或版本");
        return new WorkspaceConfiguration
        {
            SchemaVersion = WorkspaceConfiguration.CurrentSchemaVersion,
            Settings = portable.Settings ?? new AppSettings(),
            Profiles = portable.Profiles ?? new List<DeviceProfile>(),
            ServerConfig = portable.ServerConfig ?? new ModbusServerConfiguration(),
            MonitorConfig = portable.MonitorConfig ?? new MonitorConfiguration()
        };
    }

    public AppSettings LoadSettings() => LoadWorkspace().Settings;

    public void SaveSettings(AppSettings settings)
    {
        var ws = LoadWorkspace();
        ws.Settings = settings;
        SaveWorkspace(ws);
    }

    public List<DeviceProfile> LoadProfiles() => LoadWorkspace().Profiles;

    public void SaveProfiles(IEnumerable<DeviceProfile> profiles)
    {
        var ws = LoadWorkspace();
        ws.Profiles = profiles.ToList();
        SaveWorkspace(ws);
    }

    public ModbusServerConfiguration LoadServerConfig() => LoadWorkspace().ServerConfig;

    public void SaveServerConfig(ModbusServerConfiguration config)
    {
        var ws = LoadWorkspace();
        ws.ServerConfig = MigrateServerConfig(config);
        SaveWorkspace(ws);
    }

    public MonitorConfiguration LoadMonitorConfiguration() => LoadWorkspace().MonitorConfig;

    public void SaveMonitorConfiguration(MonitorConfiguration config)
    {
        var ws = LoadWorkspace();
        ws.MonitorConfig = config;
        SaveWorkspace(ws);
    }

    private WorkspaceConfiguration MigrateWorkspace(WorkspaceConfiguration ws)
    {
        bool upgraded = ws.SchemaVersion < WorkspaceConfiguration.CurrentSchemaVersion;
        ws.Settings ??= new AppSettings();
        ws.Profiles ??= DefaultProfiles();
        ws.ServerConfig = MigrateServerConfig(ws.ServerConfig ?? DefaultServerConfig());
        ws.MonitorConfig ??= new MonitorConfiguration();
        ws.SchemaVersion = WorkspaceConfiguration.CurrentSchemaVersion;

        if (upgraded)
        {
            AppLogger.Info($"已迁移工作区配置版本至 v{WorkspaceConfiguration.CurrentSchemaVersion}");
            WriteFileAtomic(WorkspaceConfigPath, JsonSerializer.Serialize(ws, JsonOptions));
        }

        return ws;
    }

    public static ModbusServerConfiguration MigrateServerConfig(ModbusServerConfiguration cfg)
    {
        cfg.Version = 2;
        cfg.BindAddress = string.IsNullOrWhiteSpace(cfg.BindAddress) ? "127.0.0.1" : cfg.BindAddress.Trim();
        cfg.Port = Math.Clamp(cfg.Port, 1, 65535);
        cfg.Station = Math.Clamp(cfg.Station, 1, 247);
        cfg.MaxClients = cfg.MaxClients <= 0 ? 32 : Math.Clamp(cfg.MaxClients, 1, 256);
        cfg.IdleTimeoutMs = cfg.IdleTimeoutMs < 0 ? 60000 : Math.Clamp(cfg.IdleTimeoutMs, 0, 3600000);
        cfg.MaxFrameLength = cfg.MaxFrameLength <= 0 ? 260 : Math.Clamp(cfg.MaxFrameLength, 64, 65536);
        cfg.SendQueueLimit = cfg.SendQueueLimit <= 0 ? 64 : Math.Clamp(cfg.SendQueueLimit, 1, 1024);
        cfg.AllowedClientIps ??= string.Empty;
        cfg.ServerSerial ??= new SerialPortParameters();
        cfg.Registers ??= new List<ServerRegisterDefinition>();
        return cfg;
    }

    private AppSettings LoadLegacySettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"读取旧版系统设置失败 ({SettingsPath})", ex);
        }
        return new AppSettings();
    }

    private List<DeviceProfile> LoadLegacyProfiles()
    {
        try
        {
            if (File.Exists(ProfilesPath))
            {
                var json = File.ReadAllText(ProfilesPath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<DeviceProfile>>(json, JsonOptions);
                if (list != null) return list;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"读取旧版设备方案失败 ({ProfilesPath})", ex);
        }
        return DefaultProfiles();
    }

    private ModbusServerConfiguration LoadLegacyServerConfig()
    {
        try
        {
            if (File.Exists(ServerConfigPath))
            {
                var json = File.ReadAllText(ServerConfigPath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<ModbusServerConfiguration>(json, JsonOptions);
                if (cfg != null) return MigrateServerConfig(cfg);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"读取旧版从站服务器配置失败 ({ServerConfigPath})", ex);
        }
        return DefaultServerConfig();
    }

    private MonitorConfiguration LoadLegacyMonitorConfiguration()
    {
        try
        {
            if (File.Exists(MonitorConfigPath))
            {
                var json = File.ReadAllText(MonitorConfigPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<MonitorConfiguration>(json, JsonOptions) ?? new MonitorConfiguration();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"读取旧版监听采集组配置失败 ({MonitorConfigPath})", ex);
        }
        return new MonitorConfiguration();
    }

    private static List<DeviceProfile> DefaultProfiles() => new()
    {
        new() { Name = "本地仿真站", Protocol = "Modbus TCP", Endpoint = "127.0.0.1:5020", StationId = 1 },
        new() { Name = "温湿度传感器", Protocol = "Modbus RTU", Endpoint = "COM3,9600", StationId = 2 }
    };

    private static ModbusServerConfiguration DefaultServerConfig() => new()
    {
        Version = 2,
        BindAddress = "127.0.0.1",
        Port = 5020,
        Station = 1,
        Registers = new List<ServerRegisterDefinition>
        {
            new() { Area = Core.Enums.ServerRegisterArea.HoldingRegister, Address = 0, DataType = Core.Enums.ServerRegisterDataType.UInt16, InitialValue = "100", CurrentValue = "100", Note = "温度设定" },
            new() { Area = Core.Enums.ServerRegisterArea.HoldingRegister, Address = 1, DataType = Core.Enums.ServerRegisterDataType.Int16, InitialValue = "-25", CurrentValue = "-25", Note = "偏移量" },
            new() { Area = Core.Enums.ServerRegisterArea.Coil, Address = 0, DataType = Core.Enums.ServerRegisterDataType.Bit, InitialValue = "true", CurrentValue = "true", Note = "运行使能" }
        }
    };

    private static void WriteFileAtomic(string targetPath, string content)
    {
        string tempPath = targetPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"写入配置文件失败 ({targetPath})", ex);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public static string SerializeServerConfig(ModbusServerConfiguration config) =>
        JsonSerializer.Serialize(MigrateServerConfig(config), JsonOptions);

    public static ModbusServerConfiguration? DeserializeServerConfig(string json)
    {
        var cfg = JsonSerializer.Deserialize<ModbusServerConfiguration>(json, JsonOptions);
        return cfg == null ? null : MigrateServerConfig(cfg);
    }
}
