using Yuandl.ModbusAssistant.Models;

namespace Yuandl.ModbusAssistant.Services;

/// <summary>
/// Normalizes externally supplied server limits before a listener starts.
/// Keeping this policy independent makes it reusable by TCP and serial hosts.
/// </summary>
public static class ModbusServerSecurityPolicy
{
    public static ModbusServerSecurityOptions Normalize(ModbusServerSecurityOptions source) => new()
    {
        BindAddress = string.IsNullOrWhiteSpace(source.BindAddress) ? "127.0.0.1" : source.BindAddress.Trim(),
        MaxClients = Math.Clamp(source.MaxClients, 1, 256),
        IdleTimeoutMs = Math.Clamp(source.IdleTimeoutMs, 0, 3600000),
        MaxFrameLength = Math.Clamp(source.MaxFrameLength, 16, 4096),
        SendQueueLimit = Math.Clamp(source.SendQueueLimit, 1, 1024),
        AllowedClientIps = source.AllowedClientIps?.Trim() ?? string.Empty
    };
}
