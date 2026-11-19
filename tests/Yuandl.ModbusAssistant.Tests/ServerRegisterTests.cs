using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yuandl.ModbusAssistant.Models.Connection;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Tests;

[TestClass]
public class ServerRegisterTests
{
    [TestMethod]
    public void EncodeRegisters_EncodesSupportedNumericTypes()
    {
        CollectionAssert.AreEqual(new ushort[] { 0xFFFE },
            ServerRegisterCodec.EncodeRegisters("-2", ServerRegisterDataType.Int16, ServerRegisterByteOrder.BigEndian));
        CollectionAssert.AreEqual(new ushort[] { 0x1234, 0x5678 },
            ServerRegisterCodec.EncodeRegisters("305419896", ServerRegisterDataType.UInt32, ServerRegisterByteOrder.BigEndian));
        CollectionAssert.AreEqual(new ushort[] { 0x7856, 0x3412 },
            ServerRegisterCodec.EncodeRegisters("305419896", ServerRegisterDataType.UInt32, ServerRegisterByteOrder.LittleEndian));
        CollectionAssert.AreEqual(new ushort[] { 0x3F80, 0x0000 },
            ServerRegisterCodec.EncodeRegisters("1", ServerRegisterDataType.Float, ServerRegisterByteOrder.BigEndian));
        Assert.AreEqual(4, ServerRegisterCodec.EncodeRegisters("1.5", ServerRegisterDataType.Double, ServerRegisterByteOrder.BigEndian).Length);
    }

    [TestMethod]
    public void Validate_RequiresDataTypeToMatchAreaAndAddressRange()
    {
        var wrongBitType = new ServerRegisterDefinition(ServerRegisterArea.Coil, 0, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "1", "");
        Assert.ThrowsException<ArgumentException>(() => ServerRegisterCodec.Validate(wrongBitType));

        var invalidBitValue = new ServerRegisterDefinition(ServerRegisterArea.DiscreteInput, 0, ServerRegisterDataType.Bit, ServerRegisterByteOrder.BigEndian, "1", "");
        Assert.ThrowsException<FormatException>(() => ServerRegisterCodec.Validate(invalidBitValue));

        var overflowingDouble = new ServerRegisterDefinition(ServerRegisterArea.HoldingRegister, ushort.MaxValue - 2, ServerRegisterDataType.Double, ServerRegisterByteOrder.BigEndian, "1", "");
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ServerRegisterCodec.Validate(overflowingDouble));
    }

    [TestMethod]
    public void ValidateNoOverlap_AllowsSeparateAreasAndRejectsSharedAddresses()
    {
        var first = new ServerRegisterDefinition(ServerRegisterArea.HoldingRegister, 10, ServerRegisterDataType.Float, ServerRegisterByteOrder.BigEndian, "1.25", "float");
        var overlapping = new ServerRegisterDefinition(ServerRegisterArea.HoldingRegister, 11, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "5", "overlap");
        var sameAddressOtherArea = new ServerRegisterDefinition(ServerRegisterArea.InputRegister, 10, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "7", "separate area");

        Assert.ThrowsException<ArgumentException>(() => ServerRegisterCodec.ValidateNoOverlap(new[] { first, overlapping }));
        ServerRegisterCodec.ValidateNoOverlap(new[] { first, sameAddressOtherArea });
    }

    [TestMethod]
    public void ConfigurationJson_RoundTripsDefinitionsAndVersion()
    {
        var configuration = new ModbusServerConfiguration(1, 5020, 3,
        [
            new ServerRegisterDefinition(ServerRegisterArea.Coil, 12, ServerRegisterDataType.Bit, ServerRegisterByteOrder.BigEndian, "true", "run"),
            new ServerRegisterDefinition(ServerRegisterArea.HoldingRegister, 100, ServerRegisterDataType.Double, ServerRegisterByteOrder.LittleEndian, "12.5", "temperature")
        ]);

        var json = JsonSerializer.Serialize(configuration);
        var restored = JsonSerializer.Deserialize<ModbusServerConfiguration>(json);

        Assert.IsNotNull(restored);
        Assert.AreEqual(1, restored.Version);
        Assert.AreEqual((byte)3, restored.Station);
        Assert.AreEqual(2, restored.Registers.Count);
        Assert.AreEqual(4, restored.Registers[1].Width);
        Assert.AreEqual("12.5", restored.Registers[1].InitialValue);
    }
}
