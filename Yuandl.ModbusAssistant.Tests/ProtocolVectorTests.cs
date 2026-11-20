using System.Globalization;
using System.IO;
using System.Text.Json;
using Xunit;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.Tests;

public class ProtocolVectorTests
{
    private static JsonDocument LoadVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test-vectors", "modbus-vectors.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test-vectors", "modbus-vectors.json"));

        Assert.True(File.Exists(path), $"Vector file not found: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void SharedProtocolVectors_FrameBuildAndParse_MatchesWebImplementation()
    {
        using var doc = LoadVectors();
        foreach (var vec in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            string name = vec.GetProperty("name").GetString()!;
            string kind = vec.GetProperty("kind").GetString()!;
            var transport = Enum.Parse<ModbusTransport>(vec.GetProperty("transport").GetString()!);
            byte unitId = vec.GetProperty("unitId").GetByte();
            byte fn = vec.GetProperty("functionCode").GetByte();
            ushort address = vec.GetProperty("address").GetUInt16();
            ushort txId = vec.GetProperty("transactionId").GetUInt16();
            string expectedHex = vec.GetProperty("expectedHex").GetString()!;

            byte[] built = kind switch
            {
                "read" => ModbusFrame.BuildRequest(unitId, fn, address, vec.GetProperty("quantity").GetUInt16(), transport, txId),
                "writeSingle" => ModbusFrame.BuildWriteSingle(unitId, fn, address, vec.GetProperty("value").GetUInt16(), transport, txId),
                "writeMultiple" => ModbusFrame.BuildWriteMultiple(
                    unitId,
                    fn,
                    address,
                    vec.GetProperty("values").EnumerateArray().Select(x => x.GetUInt16()).ToArray(),
                    transport,
                    txId),
                _ => throw new InvalidOperationException($"Unknown kind: {kind}")
            };

            Assert.Equal(expectedHex, ByteUtils.BytesToHex(built));

            var parsed = ModbusFrame.ParseRequest(built, transport);
            Assert.Equal(unitId, parsed.UnitId);
            Assert.Equal(fn, parsed.FunctionCode);
            Assert.Equal(address, parsed.Address);
            if (vec.TryGetProperty("values", out var valuesProp))
            {
                ushort[] expectedValues = valuesProp.EnumerateArray().Select(x => x.GetUInt16()).ToArray();
                Assert.Equal(expectedValues, parsed.Values);
            }
        }
    }

    [Fact]
    public void SharedProtocolVectors_ByteOrdersAndServerCodec_MatchWebImplementation()
    {
        using var doc = LoadVectors();
        foreach (var vec in doc.RootElement.GetProperty("byteOrderVectors").EnumerateArray())
        {
            string name = vec.GetProperty("name").GetString()!;
            var dataType = Enum.Parse<RegisterDataType>(vec.GetProperty("dataType").GetString()!);
            var byteOrder = Enum.Parse<RegisterByteOrder>(vec.GetProperty("byteOrder").GetString()!);
            ushort[] regs = vec.GetProperty("registers").EnumerateArray().Select(x => x.GetUInt16()).ToArray();
            double scale = vec.GetProperty("scale").GetDouble();
            double expected = vec.GetProperty("expected").GetDouble();
            double tolerance = vec.GetProperty("tolerance").GetDouble();

            double actual = RegisterValueConverter.Convert(regs, dataType, byteOrder, scale);
            Assert.True(
                Math.Abs(actual - expected) <= tolerance,
                $"{name}: expected {expected}, got {actual}");
        }

        foreach (var rt in doc.RootElement.GetProperty("serverCodecRoundTrips").EnumerateArray())
        {
            var dataType = Enum.Parse<ServerRegisterDataType>(rt.GetProperty("dataType").GetString()!);
            var byteOrder = Enum.Parse<ServerRegisterByteOrder>(rt.GetProperty("byteOrder").GetString()!);
            string value = rt.GetProperty("value").GetString()!;

            ushort[] encoded = ServerRegisterCodec.EncodeRegisters(value, dataType, byteOrder);
            string decoded = ServerRegisterCodec.DecodeRegisters(encoded, dataType, byteOrder);
            double expectedNum = double.Parse(value, CultureInfo.InvariantCulture);
            double actualNum = double.Parse(decoded, CultureInfo.InvariantCulture);
            Assert.True(
                Math.Abs(actualNum - expectedNum) <= 1e-4,
                $"Round-trip mismatch {dataType}/{byteOrder}: expected {value}, got {decoded}");
        }
    }
}
