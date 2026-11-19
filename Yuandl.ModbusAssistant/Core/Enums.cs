namespace Yuandl.ModbusAssistant.Core.Enums;

public enum ChannelState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}

public enum CommunicationType
{
    SerialPort,
    TcpClient,
    Udp,
    TcpServer
}

public enum DataEncoding
{
    Hex,
    Ascii,
    Utf8
}

public enum EndingDelimiter
{
    None,
    CR,
    LF,
    CRLF
}

public enum ModbusTransport
{
    Rtu,
    Ascii,
    Tcp
}

public enum RegisterDataType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float,
    Double
}

public enum RegisterByteOrder
{
    BigEndian,
    LittleEndian,
    WordSwap,
    ByteSwap
}

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
    LittleEndian,
    WordSwap,
    ByteSwap
}

public enum ModbusServerTransportMode
{
    Tcp,
    SerialRtu,
    SerialAscii,
    TcpAndSerial
}
