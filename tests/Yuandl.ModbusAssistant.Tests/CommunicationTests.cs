using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yuandl.ModbusAssistant.Communication;
using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Communication.Tcp;
using Yuandl.ModbusAssistant.Communication.Udp;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.Tests;

[TestClass]
public class CommunicationTests
{
    [TestMethod]
    public void ChannelStatistics_ThreadSafeIncrements_AccurateCounts()
    {
        // Arrange
        var stats = new ChannelStatistics();
        int iterations = 1000;

        // Act - 10 线程并发高频更新流量与计数
        Parallel.For(0, 10, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                stats.AddBytesSent(10);
                stats.AddBytesReceived(20);
                stats.IncrementErrors();
            }
        });

        // Assert
        Assert.AreEqual(10 * iterations * 10, stats.BytesSent);
        Assert.AreEqual(10 * iterations * 20, stats.BytesReceived);
        Assert.AreEqual(10 * iterations, stats.FramesSent);
        Assert.AreEqual(10 * iterations, stats.FramesReceived);
        Assert.AreEqual(10 * iterations, stats.ErrorsCount);

        stats.Reset();
        Assert.AreEqual(0, stats.BytesSent);
        Assert.AreEqual(0, stats.BytesReceived);
        Assert.AreEqual(0, stats.ErrorsCount);
    }

    [TestMethod]
    public async Task TcpClientChannel_ConnectSendReceive_Success()
    {
        // Arrange - 启动本地回环 TCP 监听器（动态端口）
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverSocket = await listener.AcceptSocketAsync();
            byte[] serverBuf = new byte[256];
            int read = await serverSocket.ReceiveAsync(serverBuf, SocketFlags.None);

            // 服务端原样回传 (Echo) + 追加确认字节
            await serverSocket.SendAsync(serverBuf.AsMemory(0, read), SocketFlags.None);
        });

        var clientParams = new TcpClientParameters
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeoutMs = 2000
        };

        using var channel = new TcpClientChannel(clientParams);

        var receiveTcs = new TaskCompletionSource<byte[]>();
        channel.DataReceived += (s, e) =>
        {
            receiveTcs.TrySetResult(e.Data.ToArray());
        };

        // Act
        bool opened = await channel.OpenAsync();
        Assert.IsTrue(opened, "TCP 通道应成功打开连接");
        Assert.AreEqual(ChannelState.Connected, channel.State);

        byte[] payload = Encoding.UTF8.GetBytes("PING_MODBUS");
        int sent = await channel.SendAsync(payload);
        Assert.AreEqual(payload.Length, sent);

        // Assert - 等待接收回传数据
        var received = await Task.WhenAny(receiveTcs.Task, Task.Delay(3000));
        Assert.AreEqual(receiveTcs.Task, received, "应在 3 秒内接收到回传数据");

        var receivedBytes = await receiveTcs.Task;
        CollectionAssert.AreEqual(payload, receivedBytes);

        // 验证流量计数
        Assert.AreEqual(payload.Length, channel.Statistics.BytesSent);
        Assert.AreEqual(payload.Length, channel.Statistics.BytesReceived);

        // 关闭通道
        await channel.CloseAsync();
        Assert.AreEqual(ChannelState.Disconnected, channel.State);

        listener.Stop();
        await serverTask;
    }

    [TestMethod]
    public async Task UdpChannel_ConnectSendReceive_Success()
    {
        // Arrange - 启动本地 UDP 监听端
        using var udpServer = new UdpClient(0);
        int serverPort = ((IPEndPoint)udpServer.Client.LocalEndPoint!).Port;

        var serverTask = Task.Run(async () =>
        {
            var res = await udpServer.ReceiveAsync();
            // Echo 回传
            await udpServer.SendAsync(res.Buffer, res.Buffer.Length, res.RemoteEndPoint);
        });

        var udpParams = new UdpParameters
        {
            RemoteHost = "127.0.0.1",
            RemotePort = serverPort,
            LocalPort = 0
        };

        using var channel = new UdpChannel(udpParams);
        var receiveTcs = new TaskCompletionSource<byte[]>();
        channel.DataReceived += (s, e) =>
        {
            receiveTcs.TrySetResult(e.Data.ToArray());
        };

        // Act
        bool opened = await channel.OpenAsync();
        Assert.IsTrue(opened);

        byte[] payload = Encoding.UTF8.GetBytes("UDP_RAW_TEST");
        await channel.SendAsync(payload);

        var received = await Task.WhenAny(receiveTcs.Task, Task.Delay(3000));
        Assert.AreEqual(receiveTcs.Task, received);

        var receivedBytes = await receiveTcs.Task;
        CollectionAssert.AreEqual(payload, receivedBytes);

        await channel.CloseAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task CommunicationService_ManageLifecycle_Success()
    {
        // Arrange
        var factory = new ChannelFactory();
        using var commService = new CommunicationService(factory);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var tcpParams = new TcpClientParameters
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeoutMs = 2000
        };

        // Act
        bool connected = await commService.ConnectTcpAsync(tcpParams);

        // Assert
        Assert.IsTrue(connected);
        Assert.IsTrue(commService.IsConnected);
        Assert.AreEqual(CommunicationType.TcpClient, commService.CurrentType);

        await commService.DisconnectAsync();
        Assert.IsFalse(commService.IsConnected);
        Assert.AreEqual(ChannelState.Disconnected, commService.CurrentState);

        listener.Stop();
    }
}
