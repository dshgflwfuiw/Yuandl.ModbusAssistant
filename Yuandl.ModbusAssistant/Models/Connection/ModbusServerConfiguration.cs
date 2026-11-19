namespace Yuandl.ModbusAssistant.Models.Connection;

public sealed record ModbusServerConfiguration(int Version, int Port, byte Station, IReadOnlyList<ServerRegisterDefinition> Registers);
