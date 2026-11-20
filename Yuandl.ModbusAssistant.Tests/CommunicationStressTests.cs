using System.Net;
using System.Net.Sockets;
using Xunit;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.Tests;

public class CommunicationStressTests
{
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task TcpClientChannel_MultipleReconnectCycles_PreserveReadLoopAndReceiveData()
    {
        int port = GetFreeTcpPort();
        var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();

        var receivedFrames = new List<byte[]>();
        using var channel = new TcpClientChannel(new TcpClientParameters
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeoutMs = 2000,
            AutoReconnect = true,
            ReconnectIntervalMs = 200
        });
        channel.DataReceived += (_, e) => lock (receivedFrames) { receivedFrames.Add(e.Bytes); };

        try
        {
            var accept1 = server.AcceptTcpClientAsync();
            await channel.OpenAsync();
            using var peer1 = await accept1;
            await peer1.GetStream().WriteAsync(new byte[] { 0x01, 0x02 });
            await Task.Delay(80);
            Assert.Equal(ChannelState.Connected, channel.State);

            // Cycle 1 disconnect -> auto reconnect -> verify read loop still receives
            var accept2 = server.AcceptTcpClientAsync();
            peer1.Close();
            using var peer2 = await accept2;
            await Task.Delay(80);
            await peer2.GetStream().WriteAsync(new byte[] { 0x03, 0x04 });
            await Task.Delay(80);

            // Cycle 2 disconnect -> auto reconnect -> verify read loop STILL receives!
            var accept3 = server.AcceptTcpClientAsync();
            peer2.Close();
            using var peer3 = await accept3;
            await Task.Delay(80);
            await peer3.GetStream().WriteAsync(new byte[] { 0x05, 0x06 });
            await Task.Delay(100);

            lock (receivedFrames)
            {
                var flattened = receivedFrames.SelectMany(b => b).ToArray();
                Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, flattened);
            }
        }
        finally
        {
            await channel.CloseAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task CommunicationService_StaleTxIdDiscard_FragmentedResponse_AndBroadcast_Work()
    {
        int port = GetFreeTcpPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        using var serverLogic = new ModbusServerService();
        serverLogic.ConfigureRegisters(new[]
        {
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 0,
                DataType = ServerRegisterDataType.UInt16,
                ByteOrder = ServerRegisterByteOrder.BigEndian,
                InitialValue = "123"
            }
        });

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            client.NoDelay = true;
            var stream = client.GetStream();
            var buf = new byte[1024];
            while (client.Connected)
            {
                int read = await stream.ReadAsync(buf);
                if (read <= 0) break;
                if (read < 8) continue;

                byte unitId = buf[6];
                var pdu = new byte[read - 7];
                Array.Copy(buf, 7, pdu, 0, pdu.Length);
                var respPdu = serverLogic.ProcessRequest(unitId, pdu, out bool suppress);
                if (suppress || respPdu.Length == 0) continue;

                // Inject wrong TxId frame first, then send matching response fragmented in 2 chunks
                var stale = new byte[7 + respPdu.Length];
                stale[0] = 0x03; stale[1] = 0xE7;
                stale[4] = (byte)((respPdu.Length + 1) >> 8);
                stale[5] = (byte)((respPdu.Length + 1) & 0xFF);
                stale[6] = unitId;
                Array.Copy(respPdu, 0, stale, 7, respPdu.Length);
                await stream.WriteAsync(stale);
                await Task.Delay(10);

                var real = new byte[7 + respPdu.Length];
                Array.Copy(buf, 0, real, 0, 4);
                real[4] = stale[4];
                real[5] = stale[5];
                real[6] = unitId;
                Array.Copy(respPdu, 0, real, 7, respPdu.Length);
                await stream.WriteAsync(real.AsMemory(0, 4));
                await Task.Delay(10);
                await stream.WriteAsync(real.AsMemory(4));
            }
        });

        using var comm = new CommunicationService();
        try
        {
            await comm.ConnectTcpClientAsync(new TcpClientParameters
            {
                Host = "127.0.0.1",
                Port = port,
                ConnectTimeoutMs = 2000
            });

            byte[] req = ModbusFrame.BuildRequest(1, 3, 0, 1, ModbusTransport.Tcp, transactionId: 55);
            byte[] resp = await comm.ExchangeModbusAsync(req, ModbusTransport.Tcp, timeoutMs: 1500, retries: 1);
            var parsed = ModbusFrame.ParseResponse(resp, ModbusTransport.Tcp);
            Assert.Equal((ushort)55, parsed.TransactionId);
            Assert.Equal(new ushort[] { 123 }, parsed.Registers);

            // Broadcast write must return empty without timeout
            byte[] bcast = ModbusFrame.BuildWriteSingle(0, 6, 0, 888, ModbusTransport.Tcp, transactionId: 56);
            byte[] bcastResp = await comm.ExchangeModbusAsync(bcast, ModbusTransport.Tcp, timeoutMs: 400, retries: 2);
            Assert.Empty(bcastResp);

            byte[] verifyReq = ModbusFrame.BuildRequest(1, 3, 0, 1, ModbusTransport.Tcp, transactionId: 57);
            byte[] verifyResp = await comm.ExchangeModbusAsync(verifyReq, ModbusTransport.Tcp, timeoutMs: 1500, retries: 1);
            Assert.Equal(new ushort[] { 888 }, ModbusFrame.ParseResponse(verifyResp, ModbusTransport.Tcp).Registers);
        }
        finally
        {
            await comm.DisconnectAsync();
            listener.Stop();
            try { await serverTask; } catch { }
        }
    }

    [Fact]
    public async Task UdpChannel_LoopbackSendAndCancelClose_WorksCleanly()
    {
        using var echo = new UdpClient(0);
        int echoPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource();

        var echoTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var r = await echo.ReceiveAsync(cts.Token);
                await echo.SendAsync(r.Buffer, r.RemoteEndPoint, cts.Token);
            }
        });

        using var comm = new CommunicationService();
        try
        {
            await comm.ConnectUdpAsync(new UdpParameters
            {
                RemoteHost = "127.0.0.1",
                RemotePort = echoPort,
                LocalPort = 0
            });

            byte[] req = ModbusFrame.BuildRequest(1, 3, 0, 1, ModbusTransport.Rtu);
            byte[] resp = await comm.ExchangeModbusAsync(req, ModbusTransport.Rtu, timeoutMs: 1000, retries: 1);
            Assert.Equal(req, resp);
        }
        finally
        {
            await cts.CancelAsync();
            await comm.DisconnectAsync();
            try { await echoTask; } catch { }
        }
    }
}
