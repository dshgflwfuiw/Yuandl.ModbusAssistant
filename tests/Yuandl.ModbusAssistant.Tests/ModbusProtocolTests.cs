using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Tests;

[TestClass]
public class ModbusProtocolTests
{
    [TestMethod]
    public void RtuRequest_UsesStandardCrcAndRoundTripsResponse()
    {
        var request = ModbusFrame.BuildRequest(1, 3, 0, 2);
        CollectionAssert.AreEqual(new byte[] { 1, 3, 0, 0, 0, 2, 0xC4, 0x0B }, request);
        var response = ModbusFrame.ParseResponse(ModbusFrame.Encode(new byte[] { 1, 3, 4, 0, 10, 0, 20 }, ModbusTransport.Rtu), ModbusTransport.Rtu);
        CollectionAssert.AreEqual(new ushort[] { 10, 20 }, response.Registers);
    }

    [TestMethod]
    public void AsciiAndTcpResponses_ValidateFraming()
    {
        var ascii = ModbusFrame.Encode(new byte[] { 1, 3, 2, 0, 10 }, ModbusTransport.Ascii);
        Assert.AreEqual(10, ModbusFrame.ParseResponse(ascii, ModbusTransport.Ascii).Registers[0]);
        var tcp = ModbusFrame.Encode(new byte[] { 1, 3, 2, 0, 10 }, ModbusTransport.Tcp);
        Assert.AreEqual(10, ModbusFrame.ParseResponse(tcp, ModbusTransport.Tcp).Registers[0]);
    }

    [TestMethod]
    public void ParseResponse_RejectsTruncatedAndExceptionFrames()
    {
        Assert.ThrowsException<FormatException>(() =>
            ModbusFrame.ParseResponse(new byte[] { 1, 3, 0 }, ModbusTransport.Rtu));

        var exceptionResponse = ModbusFrame.Encode(new byte[] { 1, 0x83, 2 }, ModbusTransport.Rtu);
        var parsed = ModbusFrame.ParseResponse(exceptionResponse, ModbusTransport.Rtu);
        Assert.IsTrue(parsed.IsException);
        Assert.AreEqual((byte)2, parsed.ExceptionCode);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            ModbusFrame.BuildWriteMultiple(1, 16, 0, [], ModbusTransport.Rtu));
    }

    [TestMethod]
    public void TcpResponse_RejectsInvalidLengthAndProtocolFields()
    {
        var invalidProtocol = new byte[] { 1, 0, 0, 1, 0, 6, 1, 3, 2, 0, 10 };
        Assert.ThrowsException<FormatException>(() =>
            ModbusFrame.ParseResponse(invalidProtocol, ModbusTransport.Tcp));

        var invalidLength = new byte[] { 1, 0, 0, 0, 0, 4, 1, 3, 2, 0, 10 };
        Assert.ThrowsException<FormatException>(() =>
            ModbusFrame.ParseResponse(invalidLength, ModbusTransport.Tcp));
    }

    [TestMethod]
    public void RegisterConverter_HandlesFloatAndScaling()
    {
        var value = RegisterValueConverter.Convert(new ushort[] { 0x3F80, 0x0000 }, RegisterDataType.Float, RegisterByteOrder.BigEndian, 2);
        Assert.AreEqual(2d, (double)value, 0.0001);
    }
}
