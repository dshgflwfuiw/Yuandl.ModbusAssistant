namespace Yuandl.ModbusAssistant.Models.Connection;

public enum ServerRegisterArea
{
    HoldingRegister,
    InputRegister,
    Coil,
    DiscreteInput
}

public enum ServerRegisterDataType
{
    Bit,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float,
    Double
}

public enum ServerRegisterByteOrder
{
    BigEndian,
    LittleEndian
}

public sealed record ServerRegisterDefinition(
    ServerRegisterArea Area,
    ushort Address,
    ServerRegisterDataType DataType,
    ServerRegisterByteOrder ByteOrder,
    string InitialValue,
    string Note)
{
    public int Width => DataType switch
    {
        ServerRegisterDataType.Int32 or ServerRegisterDataType.UInt32 or ServerRegisterDataType.Float => 2,
        ServerRegisterDataType.Double => 4,
        _ => 1
    };
}
