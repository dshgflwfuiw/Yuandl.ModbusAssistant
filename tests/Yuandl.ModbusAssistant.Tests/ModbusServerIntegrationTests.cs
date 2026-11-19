using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yuandl.ModbusAssistant.Models.Connection;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.Tests;

[TestClass]
public class ModbusServerIntegrationTests
{
    [TestMethod]
    public async Task Server_ExposesHoldingInputAndCoilDefinitionsOverTcp()
    {
        var port = GetAvailablePort();
        using var server = new HslModbusServerService();
        server.ConfigureRegisters(
        [
            new ServerRegisterDefinition(ServerRegisterArea.HoldingRegister, 100, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "4660", "holding"),
            new ServerRegisterDefinition(ServerRegisterArea.InputRegister, 200, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "22136", "input"),
            new ServerRegisterDefinition(ServerRegisterArea.Coil, 300, ServerRegisterDataType.Bit, ServerRegisterByteOrder.BigEndian, "true", "coil"),
            new ServerRegisterDefinition(ServerRegisterArea.DiscreteInput, 400, ServerRegisterDataType.Bit, ServerRegisterByteOrder.BigEndian, "true", "discrete")
        ]);
        server.Start(port, 1);

        var holding = await SendRequestAsync(port, 3, 100, 1);
        Assert.AreEqual((ushort)0x1234, ModbusFrame.ParseResponse(holding, ModbusTransport.Tcp).Registers[0]);

        var input = await SendRequestAsync(port, 4, 200, 1);
        Assert.AreEqual((ushort)0x5678, ModbusFrame.ParseResponse(input, ModbusTransport.Tcp).Registers[0]);

        var coil = await SendRequestAsync(port, 1, 300, 1);
        Assert.AreNotEqual(0, coil[^1] & 1, "Configured coil should read as true");

        var discrete = await SendRequestAsync(port, 2, 400, 1);
        Assert.AreNotEqual(0, discrete[^1] & 1, "Configured discrete input should read as true");

        var writeHolding = await SendRawRequestAsync(port, ModbusFrame.BuildWriteSingle(1, 6, 100, 0xABCD, ModbusTransport.Tcp, 2));
        Assert.AreEqual((byte)6, writeHolding[7]);
        var updatedHolding = await SendRequestAsync(port, 3, 100, 1);
        Assert.AreEqual((ushort)0xABCD, ModbusFrame.ParseResponse(updatedHolding, ModbusTransport.Tcp).Registers[0]);

        var writeInput = await SendRawRequestAsync(port, ModbusFrame.BuildWriteSingle(1, 6, 200, 0x1111, ModbusTransport.Tcp, 3));
        var exception = ModbusFrame.ParseResponse(writeInput, ModbusTransport.Tcp);
        Assert.IsTrue(exception.IsException, "Input registers must reject writes");
        Assert.AreEqual((byte)2, exception.ExceptionCode);

        server.ConfigureRegisters(
        [
            new ServerRegisterDefinition(ServerRegisterArea.HoldingRegister, 100, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "4660", "holding"),
            new ServerRegisterDefinition(ServerRegisterArea.HoldingRegister, 101, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "2", "added while running"),
            new ServerRegisterDefinition(ServerRegisterArea.InputRegister, 200, ServerRegisterDataType.UInt16, ServerRegisterByteOrder.BigEndian, "22136", "input"),
            new ServerRegisterDefinition(ServerRegisterArea.Coil, 300, ServerRegisterDataType.Bit, ServerRegisterByteOrder.BigEndian, "true", "coil"),
            new ServerRegisterDefinition(ServerRegisterArea.DiscreteInput, 400, ServerRegisterDataType.Bit, ServerRegisterByteOrder.BigEndian, "true", "discrete")
        ]);
        var preservedHolding = await SendRequestAsync(port, 3, 100, 1);
        Assert.AreEqual((ushort)0xABCD, ModbusFrame.ParseResponse(preservedHolding, ModbusTransport.Tcp).Registers[0]);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<byte[]> SendRequestAsync(int port, byte functionCode, ushort address, ushort quantity)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        var request = ModbusFrame.BuildRequest(1, functionCode, address, quantity, ModbusTransport.Tcp, 1);
        await stream.WriteAsync(request);

        var header = new byte[6];
        await ReadExactlyAsync(stream, header);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        var response = new byte[header.Length + length];
        header.CopyTo(response, 0);
        await ReadExactlyAsync(stream, response.AsMemory(header.Length, length));
        return response;
    }

    private static async Task<byte[]> SendRawRequestAsync(int port, byte[] request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        await stream.WriteAsync(request);

        var header = new byte[6];
        await ReadExactlyAsync(stream, header);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        var response = new byte[header.Length + length];
        header.CopyTo(response, 0);
        await ReadExactlyAsync(stream, response.AsMemory(header.Length, length));
        return response;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0) throw new IOException("Modbus TCP 服务器提前关闭连接");
            offset += read;
        }
    }
}
