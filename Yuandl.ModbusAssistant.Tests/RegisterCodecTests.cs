using System.Buffers.Binary;
using Xunit;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Tests;

public class RegisterCodecTests
{
    [Theory]
    [InlineData(ServerRegisterByteOrder.BigEndian, RegisterByteOrder.BigEndian)]
    [InlineData(ServerRegisterByteOrder.LittleEndian, RegisterByteOrder.LittleEndian)]
    [InlineData(ServerRegisterByteOrder.WordSwap, RegisterByteOrder.WordSwap)]
    [InlineData(ServerRegisterByteOrder.ByteSwap, RegisterByteOrder.ByteSwap)]
    public void ServerCodec_And_ClientConverter_AgreeAcrossAllByteOrders_ForFloatAndDouble(
        ServerRegisterByteOrder serverOrder,
        RegisterByteOrder clientOrder)
    {
        ushort[] floatRegs = ServerRegisterCodec.EncodeRegisters("123.5", ServerRegisterDataType.Float, serverOrder);
        double decodedFloat = RegisterValueConverter.Convert(floatRegs, RegisterDataType.Float, clientOrder, 1.0);
        Assert.Equal(123.5, decodedFloat, 4);

        ushort[] doubleRegs = ServerRegisterCodec.EncodeRegisters("-9876.125", ServerRegisterDataType.Double, serverOrder);
        double decodedDouble = RegisterValueConverter.Convert(doubleRegs, RegisterDataType.Double, clientOrder, 1.0);
        Assert.Equal(-9876.125, decodedDouble, 6);
    }

    [Fact]
    public void DoubleWordSwap_Reverses16BitWords_ExactlyMatchingSpec()
    {
        // Big-endian IEEE-754 bytes for 1.0 are 3F F0 00 00 00 00 00 00 => words [3FF0, 0000, 0000, 0000]
        // Under WordSwap (GHEFCDAB), words reverse to [0000, 0000, 0000, 3FF0]
        ushort[] regs = ServerRegisterCodec.EncodeRegisters("1", ServerRegisterDataType.Double, ServerRegisterByteOrder.WordSwap);
        Assert.Equal(new ushort[] { 0x0000, 0x0000, 0x0000, 0x3FF0 }, regs);
    }

    [Fact]
    public void ValidateNoOverlap_RejectsOverlapping32BitRegister()
    {
        var defs = new[]
        {
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 0,
                DataType = ServerRegisterDataType.Float,
                ByteOrder = ServerRegisterByteOrder.BigEndian,
                InitialValue = "10.5"
            },
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 1,
                DataType = ServerRegisterDataType.UInt16,
                ByteOrder = ServerRegisterByteOrder.BigEndian,
                InitialValue = "1"
            }
        };

        Assert.Throws<InvalidOperationException>(() => ServerRegisterCodec.ValidateNoOverlap(defs));
    }
}
