using Xunit;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.Tests;

public class ModbusServerServiceTests
{
    private static ModbusServerService CreateConfiguredServer()
    {
        var server = new ModbusServerService();
        server.ConfigureRegisters(new[]
        {
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.Coil,
                Address = 0,
                DataType = ServerRegisterDataType.Bit,
                InitialValue = "false"
            },
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.Coil,
                Address = 1,
                DataType = ServerRegisterDataType.Bit,
                InitialValue = "true"
            },
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 0,
                DataType = ServerRegisterDataType.UInt16,
                ByteOrder = ServerRegisterByteOrder.BigEndian,
                InitialValue = "42"
            },
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 1,
                DataType = ServerRegisterDataType.UInt16,
                ByteOrder = ServerRegisterByteOrder.BigEndian,
                InitialValue = "100"
            }
        });
        return server;
    }

    [Fact]
    public void ProcessRequest_ReadAndWriteSingleAndMultipleRegisters_Works()
    {
        using var server = CreateConfiguredServer();

        // Read HR 0..1 (count 2)
        byte[] readResp = server.ProcessRequest(1, new byte[] { 0x03, 0x00, 0x00, 0x00, 0x02 }, out bool suppress);
        Assert.False(suppress);
        Assert.Equal(new byte[] { 0x03, 0x04, 0x00, 0x2A, 0x00, 0x64 }, readResp);

        // FC16 write HR 0..1 = [77, 88]
        byte[] writeResp = server.ProcessRequest(1, new byte[] { 0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x00, 0x4D, 0x00, 0x58 }, out suppress);
        Assert.False(suppress);
        Assert.Equal(new byte[] { 0x10, 0x00, 0x00, 0x00, 0x02 }, writeResp);

        var values = server.GetDefinitionsWithCurrentValues();
        Assert.Equal("77", values.Single(v => v.Area == ServerRegisterArea.HoldingRegister && v.Address == 0).CurrentValue);
        Assert.Equal("88", values.Single(v => v.Area == ServerRegisterArea.HoldingRegister && v.Address == 1).CurrentValue);
    }

    [Fact]
    public void ProcessRequest_BroadcastWrite_ExecutesSilentlyWithSuppressReply()
    {
        using var server = CreateConfiguredServer();

        // Broadcast write single register addr 0 = 999
        byte[] resp = server.ProcessRequest(0, new byte[] { 0x06, 0x00, 0x00, 0x03, 0xE7 }, out bool suppress);
        Assert.True(suppress);
        Assert.Empty(resp);

        var values = server.GetDefinitionsWithCurrentValues();
        Assert.Equal("999", values.Single(v => v.Area == ServerRegisterArea.HoldingRegister && v.Address == 0).CurrentValue);
    }

    [Fact]
    public void ProcessRequest_BroadcastRead_IgnoredWithoutReply()
    {
        using var server = CreateConfiguredServer();
        byte[] resp = server.ProcessRequest(0, new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01 }, out bool suppress);
        Assert.True(suppress);
        Assert.Empty(resp);
    }

    [Fact]
    public void ProcessRequest_WrongStation_AndUnsupportedFunction_ReturnExpectedExceptions()
    {
        using var server = CreateConfiguredServer();

        byte[] wrongStation = server.ProcessRequest(9, new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01 }, out _);
        Assert.Equal(new byte[] { 0x83, 0x0B }, wrongStation);

        byte[] unsupported = server.ProcessRequest(1, new byte[] { 0x08, 0x00, 0x00, 0x00, 0x00 }, out _);
        Assert.Equal(new byte[] { 0x88, 0x01 }, unsupported);
    }

    [Fact]
    public void SetRuntimeValue_And_ResetToInitialValues_RestoreDefaults()
    {
        using var server = CreateConfiguredServer();
        server.SetRuntimeValue(ServerRegisterArea.HoldingRegister, 0, "555");
        Assert.Equal("555", server.GetDefinitionsWithCurrentValues().Single(v => v.Area == ServerRegisterArea.HoldingRegister && v.Address == 0).CurrentValue);

        server.ResetToInitialValues();
        Assert.Equal("42", server.GetDefinitionsWithCurrentValues().Single(v => v.Area == ServerRegisterArea.HoldingRegister && v.Address == 0).CurrentValue);
    }
}
