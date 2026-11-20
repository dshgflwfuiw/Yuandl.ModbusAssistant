using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Xunit;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ModbusAssistant.ViewModels;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Appearance;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Tests;

public class ServerSecurityAndLifecycleTests
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
    public void ThemeUiStartupVerifier_AssemblyAndTypesExist_And_RejectsEmptyDictionary()
    {
        ThemeUiStartupVerifier.VerifyOrThrow(appResources: null);
        Assert.Equal("Yuandl.ThemeUI", typeof(FluentWindow).Assembly.GetName().Name);
        Assert.NotNull(typeof(ApplicationThemeManager));

        var emptyResources = new ResourceDictionary();
        Assert.Throws<InvalidOperationException>(() => ThemeUiStartupVerifier.VerifyOrThrow(emptyResources));
    }

    [Fact]
    public async Task ModbusServerService_SecurityLimits_EnforceMaxClientsAllowlistIdleTimeoutAndMaxFrame()
    {
        using var server = new ModbusServerService();
        server.ConfigureRegisters(new[]
        {
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 0,
                DataType = ServerRegisterDataType.UInt16,
                InitialValue = "100"
            }
        });

        int blockedPort = GetFreeTcpPort();
        await server.StartAsync(blockedPort, 1, ModbusServerTransportMode.Tcp, securityOptions: new ModbusServerSecurityOptions
        {
            BindAddress = "127.0.0.1",
            AllowedClientIps = "192.0.2.50",
            MaxClients = 2,
            IdleTimeoutMs = 5000,
            MaxFrameLength = 64
        });

        using (var rejectedClient = new TcpClient())
        {
            await rejectedClient.ConnectAsync(IPAddress.Loopback, blockedPort);
            byte[] req = ModbusFrame.BuildRequest(1, 3, 0, 1, ModbusTransport.Tcp, transactionId: 1);
            await rejectedClient.GetStream().WriteAsync(req);
            var buf = new byte[64];
            int n = await rejectedClient.GetStream().ReadAsync(buf);
            Assert.Equal(0, n);
        }
        await server.StopAsync();

        int port = GetFreeTcpPort();
        await server.StartAsync(port, 1, ModbusServerTransportMode.Tcp, securityOptions: new ModbusServerSecurityOptions
        {
            BindAddress = "127.0.0.1",
            AllowedClientIps = "127.0.0.1",
            MaxClients = 2,
            IdleTimeoutMs = 150,
            MaxFrameLength = 32
        });

        using var c1 = new TcpClient();
        await c1.ConnectAsync(IPAddress.Loopback, port);
        using var c2 = new TcpClient();
        await c2.ConnectAsync(IPAddress.Loopback, port);

        await Task.Delay(50);
        Assert.Equal(2, server.GetTcpClients().Count);

        // 3rd client exceeds MaxClients=2
        using var c3 = new TcpClient();
        await c3.ConnectAsync(IPAddress.Loopback, port);
        byte[] probe = new byte[8];
        int c3Read = await c3.GetStream().ReadAsync(probe);
        Assert.Equal(0, c3Read);

        // Wait past IdleTimeoutMs=150ms
        await Task.Delay(250);
        Assert.Empty(server.GetTcpClients());

        // Oversized packet exceeding MaxFrameLength=32 is dropped
        using var c4 = new TcpClient();
        await c4.ConnectAsync(IPAddress.Loopback, port);
        var oversized = new byte[40];
        oversized[4] = 0x00;
        oversized[5] = 34; // total=40 > 32
        await c4.GetStream().WriteAsync(oversized);
        int c4Read = await c4.GetStream().ReadAsync(probe);
        Assert.Equal(0, c4Read);

        await server.StopAsync();
    }

    [Fact]
    public async Task TcpServerChannel_SecurityLimits_EnforceMaxClientsAndOversizedFrame()
    {
        int port = GetFreeTcpPort();
        using var channel = new TcpServerChannel(new TcpServerParameters
        {
            BindAddress = "127.0.0.1",
            ListenPort = port,
            MaxClients = 1,
            IdleTimeoutMs = 2000,
            MaxFrameLength = 64,
            SendQueueLimit = 8,
            AllowedClientIps = "127.0.0.1"
        });

        await channel.OpenAsync();
        try
        {
            using var c1 = new TcpClient();
            await c1.ConnectAsync(IPAddress.Loopback, port);
            await Task.Delay(60);
            Assert.Single(channel.GetClients());

            using var c2 = new TcpClient();
            await c2.ConnectAsync(IPAddress.Loopback, port);
            int c2Read = await c2.GetStream().ReadAsync(new byte[16]);
            Assert.Equal(0, c2Read);

            // Oversized payload > 64 bytes closes c1
            await c1.GetStream().WriteAsync(new byte[128]);
            await Task.Delay(80);
            Assert.Empty(channel.GetClients());
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    [Fact]
    public async Task CommunicationService_ConcurrentTcpMultiplexing_PreservesOriginalTxIds()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusServerService();
        server.ConfigureRegisters(new[]
        {
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 0,
                DataType = ServerRegisterDataType.Float,
                ByteOrder = ServerRegisterByteOrder.WordSwap,
                InitialValue = "42.5"
            }
        });
        await server.StartAsync(port, 1, ModbusServerTransportMode.Tcp);

        using var comm = new CommunicationService();
        try
        {
            await comm.ConnectTcpClientAsync(new TcpClientParameters
            {
                Host = "127.0.0.1",
                Port = port,
                ConnectTimeoutMs = 2000
            });

            // Fire 24 concurrent requests ALL passing transactionId=1 to verify wire TxId allocation & restoration
            var tasks = Enumerable.Range(0, 24).Select(async _ =>
            {
                byte[] req = ModbusFrame.BuildRequest(1, 3, 0, 2, ModbusTransport.Tcp, transactionId: 1);
                byte[] resp = await comm.ExchangeModbusAsync(req, ModbusTransport.Tcp, timeoutMs: 2000, retries: 1);
                var parsed = ModbusFrame.ParseResponse(resp, ModbusTransport.Tcp);
                Assert.Equal((ushort)1, parsed.TransactionId);
                double val = RegisterValueConverter.Convert(parsed.Registers, RegisterDataType.Float, RegisterByteOrder.WordSwap);
                Assert.InRange(val, 42.49, 42.51);
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            await comm.DisconnectAsync();
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task CommunicationService_VirtualLoopbackSerial_RtuAndAscii_And_Cancellation()
    {
        using var server = new ModbusServerService();
        server.ConfigureRegisters(new[]
        {
            new ServerRegisterDefinition
            {
                Area = ServerRegisterArea.HoldingRegister,
                Address = 0,
                DataType = ServerRegisterDataType.UInt16,
                InitialValue = "321"
            }
        });

        var serialParams = new SerialPortParameters
        {
            PortName = "VIRTUAL_COM_TEST",
            BaudRate = 9600,
            DataBits = 8
        };

        await server.StartAsync(0, 1, ModbusServerTransportMode.TcpAndSerial, serialParams, allowLoopbackFallback: true);
        using var comm = new CommunicationService();
        try
        {
            await comm.ConnectSerialAsync(serialParams, server, allowVirtualLoopback: true);
            Assert.True(comm.IsVirtualLoopback);

            byte[] rtuReq = ModbusFrame.BuildRequest(1, 3, 0, 1, ModbusTransport.Rtu);
            byte[] rtuResp = await comm.ExchangeModbusAsync(rtuReq, ModbusTransport.Rtu, timeoutMs: 1000, retries: 0);
            Assert.Equal(new ushort[] { 321 }, ModbusFrame.ParseResponse(rtuResp, ModbusTransport.Rtu).Registers);

            byte[] asciiReq = ModbusFrame.BuildRequest(1, 3, 0, 1, ModbusTransport.Ascii);
            byte[] asciiResp = await comm.ExchangeModbusAsync(asciiReq, ModbusTransport.Ascii, timeoutMs: 1000, retries: 0);
            Assert.Equal(new ushort[] { 321 }, ModbusFrame.ParseResponse(asciiResp, ModbusTransport.Ascii).Registers);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                comm.ExchangeModbusAsync(rtuReq, ModbusTransport.Rtu, timeoutMs: 1000, retries: 0, cancellationToken: cancelled.Token));
        }
        finally
        {
            await comm.DisconnectAsync();
            await server.StopAsync();
        }
    }

    [Fact]
    public void PersistenceService_MigratesV1SplitFilesToVersion2Workspace()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"yuandl-persist-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "settings.json"), "{\"isDarkTheme\":true,\"defaultTimeoutMs\":1500,\"defaultRetryTimes\":3}");
            File.WriteAllText(Path.Combine(tempDir, "server-config.json"), "{\"version\":1,\"port\":5502,\"station\":3,\"registers\":[]}");

            var svc = new PersistenceService(tempDir);
            var ws = svc.LoadWorkspace();

            Assert.Equal(WorkspaceConfiguration.CurrentSchemaVersion, ws.SchemaVersion);
            Assert.True(ws.Settings.IsDarkTheme);
            Assert.Equal(1500, ws.Settings.DefaultTimeoutMs);
            Assert.Equal(2, ws.ServerConfig.Version);
            Assert.Equal("127.0.0.1", ws.ServerConfig.BindAddress);
            Assert.Equal(5502, ws.ServerConfig.Port);
            Assert.Equal(3, ws.ServerConfig.Station);
            Assert.True(File.Exists(Path.Combine(tempDir, "workspace-config.json")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConnectionViewModel_CsvImport_HandlesBomMultilineAndRejectsOverlap()
    {
        string csvWithBomAndMultiline = "\uFEFFArea,Address,DataType,ByteOrder,InitialValue,CurrentValue,Note\r\n" +
            "HoldingRegister,0,Float,WordSwap,25.5,25.5,\"Line1\nLine2, \"\"Quoted\"\"\"\r\n" +
            "Coil,0,Bit,BigEndian,true,true,RunFlag\r\n";

        var defs = ConnectionViewModel.ParseRegisterDefinitionsFromCsv(csvWithBomAndMultiline);
        Assert.Equal(2, defs.Count);
        Assert.Equal("Line1\nLine2, \"Quoted\"", defs[0].Note);
        Assert.Equal(ServerRegisterArea.Coil, defs[1].Area);

        string overlappingCsv = "Area,Address,DataType,ByteOrder,InitialValue,Note\n" +
            "HoldingRegister,0,Float,BigEndian,12.5,temp\n" +
            "HoldingRegister,1,UInt16,BigEndian,10,overlap\n";
        Assert.ThrowsAny<Exception>(() => ConnectionViewModel.ParseRegisterDefinitionsFromCsv(overlappingCsv));
    }

    [Fact]
    public async Task AsyncRelayCommand_ExecuteAsync_CapturesExceptionWithoutCrashing()
    {
        Exception? captured = null;
        var cmd = new AsyncRelayCommand(
            async () =>
            {
                await Task.Delay(10);
                throw new InvalidOperationException("模拟异步命令异常");
            },
            onError: ex => captured = ex);

        Assert.True(cmd.CanExecute(null));
        await cmd.ExecuteAsync();
        Assert.NotNull(captured);
        Assert.Contains("模拟异步命令异常", captured!.Message);
        Assert.True(cmd.CanExecute(null));
    }
}
