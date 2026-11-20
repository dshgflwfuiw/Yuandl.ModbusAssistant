using System.IO;
using System.Text;
using Xunit;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Tests;

public class ModbusFrameTests
{
    [Fact]
    public void BuildAndParse_RtuReadHoldingRegisters_RoundTrips()
    {
        byte[] req = ModbusFrame.BuildRequest(1, 3, 0x0010, 2, ModbusTransport.Rtu);
        Assert.Equal(8, req.Length);
        Assert.True(Crc16.Verify(req));

        var parsed = ModbusFrame.ParseRequest(req, ModbusTransport.Rtu);
        Assert.Equal(1, parsed.UnitId);
        Assert.Equal(3, parsed.FunctionCode);
        Assert.Equal(0x0010, parsed.Address);
        Assert.Equal((ushort)2, parsed.Quantity);
    }

    [Fact]
    public void BuildAndParse_TcpWriteMultipleRegisters_RoundTrips()
    {
        ushort[] values = { 100, 200, 0x1234 };
        byte[] req = ModbusFrame.BuildWriteMultiple(2, 16, 10, values, ModbusTransport.Tcp, transactionId: 42);

        var parsed = ModbusFrame.ParseRequest(req, ModbusTransport.Tcp);
        Assert.Equal((ushort)42, parsed.TransactionId);
        Assert.Equal(2, parsed.UnitId);
        Assert.Equal(16, parsed.FunctionCode);
        Assert.Equal(10, parsed.Address);
        Assert.Equal((ushort)3, parsed.Quantity);
        Assert.Equal(values, parsed.Values);
    }

    [Fact]
    public void BuildAndParse_AsciiWriteMultipleCoils_RoundTrips()
    {
        ushort[] coils = { 1, 0, 1, 1, 0, 0, 1, 0, 1 };
        byte[] req = ModbusFrame.BuildWriteMultiple(1, 15, 5, coils, ModbusTransport.Ascii);
        string ascii = Encoding.ASCII.GetString(req);
        Assert.StartsWith(":", ascii);
        Assert.EndsWith("\r\n", ascii);

        var parsed = ModbusFrame.ParseRequest(req, ModbusTransport.Ascii);
        Assert.Equal(1, parsed.UnitId);
        Assert.Equal(15, parsed.FunctionCode);
        Assert.Equal(5, parsed.Address);
        Assert.Equal((ushort)9, parsed.Quantity);
        Assert.Equal(coils, parsed.Values);
    }

    [Fact]
    public void ParseResponse_CorruptedCrc_ThrowsInvalidDataException()
    {
        byte[] resp = { 0x01, 0x03, 0x02, 0x00, 0x0A, 0xFF, 0xFF };
        Assert.Throws<InvalidDataException>(() => ModbusFrame.ParseResponse(resp, ModbusTransport.Rtu));
    }

    [Fact]
    public void ParseMultiValues_ParsesCoilsAndHexRegisters()
    {
        ushort[] coils = ModbusFrame.ParseMultiValues("true, 0, on, false", isCoils: true);
        Assert.Equal(new ushort[] { 1, 0, 1, 0 }, coils);

        ushort[] regs = ModbusFrame.ParseMultiValues("10 0x00FF 65535", isCoils: false);
        Assert.Equal(new ushort[] { 10, 255, 65535 }, regs);
    }
}
