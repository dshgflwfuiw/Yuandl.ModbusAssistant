namespace Yuandl.ModbusAssistant.Protocol.Modbus.Enums;

/// <summary>
/// 标准 Modbus 功能码枚举
/// </summary>
public enum ModbusFunctionCode : byte
{
    /// <summary>
    /// 01 (0x01)：读线圈状态 (Read Coils)
    /// </summary>
    ReadCoils = 0x01,

    /// <summary>
    /// 02 (0x02)：读离散输入状态 (Read Discrete Inputs)
    /// </summary>
    ReadDiscreteInputs = 0x02,

    /// <summary>
    /// 03 (0x03)：读保持寄存器 (Read Holding Registers)
    /// </summary>
    ReadHoldingRegisters = 0x03,

    /// <summary>
    /// 04 (0x04)：读输入寄存器 (Read Input Registers)
    /// </summary>
    ReadInputRegisters = 0x04,

    /// <summary>
    /// 05 (0x05)：写单个线圈 (Write Single Coil)
    /// </summary>
    WriteSingleCoil = 0x05,

    /// <summary>
    /// 06 (0x06)：写单个寄存器 (Write Single Register)
    /// </summary>
    WriteSingleRegister = 0x06,

    /// <summary>
    /// 15 (0x0F)：写多个线圈 (Write Multiple Coils)
    /// </summary>
    WriteMultipleCoils = 0x0F,

    /// <summary>
    /// 16 (0x10)：写多个寄存器 (Write Multiple Registers)
    /// </summary>
    WriteMultipleRegisters = 0x10
}
