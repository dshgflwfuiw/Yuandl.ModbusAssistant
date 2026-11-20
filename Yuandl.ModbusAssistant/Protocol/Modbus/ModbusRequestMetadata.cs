using System.Text;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;

namespace Yuandl.ModbusAssistant.Protocol.Modbus;

/// <summary>
/// Extracts routing metadata without coupling transport orchestration to frame parsing.
/// </summary>
public static class ModbusRequestMetadata
{
    public static bool TryExtract(
        ReadOnlySpan<byte> request,
        ModbusTransport transport,
        out byte unitId,
        out byte functionCode,
        out ushort? transactionId)
    {
        unitId = 0;
        functionCode = 0;
        transactionId = null;

        if (transport == ModbusTransport.Tcp)
        {
            if (request.Length < 8) return false;
            transactionId = (ushort)((request[0] << 8) | request[1]);
            unitId = request[6];
            functionCode = request[7];
            return true;
        }

        if (transport == ModbusTransport.Ascii)
        {
            if (request.Length < 7 || request[0] != (byte)':') return false;
            try
            {
                string hex = Encoding.ASCII.GetString(request[1..^2]);
                var raw = ByteUtils.HexToBytes(hex);
                if (raw.Length < 3) return false;
                unitId = raw[0];
                functionCode = raw[1];
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (request.Length < 4) return false;
        unitId = request[0];
        functionCode = request[1];
        return true;
    }
}
