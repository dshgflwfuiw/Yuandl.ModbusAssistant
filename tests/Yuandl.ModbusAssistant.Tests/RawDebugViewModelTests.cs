using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yuandl.ModbusAssistant.Communication.Abstractions;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models.Connection;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Services;

namespace Yuandl.ModbusAssistant.Tests;

/// <summary>
/// 纯内存通讯模拟桩，用于精确验证 Raw 模式收发与协议隔离
/// </summary>
public class MockCommunicationService : ICommunicationService
{
    public IChannel? ActiveChannel => null;
    public bool IsConnected { get; set; } = true;
    public ChannelState CurrentState => IsConnected ? ChannelState.Connected : ChannelState.Disconnected;
    public CommunicationType? CurrentType => CommunicationType.TcpClient;
    public string CurrentChannelInfo => "MockChannel";
    public ChannelStatistics Statistics { get; } = new();

    public byte[]? LastSentBytes { get; private set; }

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler<ChannelStateChangedEventArgs>? StateChanged { add { } remove { } }
    public event EventHandler<Exception>? ErrorOccurred { add { } remove { } }

    public Task<bool> ConnectSerialAsync(SerialPortParameters parameters, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> ConnectTcpAsync(TcpClientParameters parameters, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> ConnectUdpAsync(UdpParameters parameters, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DisconnectAsync() => Task.CompletedTask;

    public Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        LastSentBytes = data.ToArray();
        Statistics.AddBytesSent(data.Length);
        return Task.FromResult(data.Length);
    }

    public void RaiseDataReceived(byte[] data)
    {
        Statistics.AddBytesReceived(data.Length);
        DataReceived?.Invoke(this, new DataReceivedEventArgs(data));
    }

    public void ResetStatistics() => Statistics.Reset();
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class MockSnackbarService : ISnackbarService
{
    public TimeSpan DefaultTimeOut { get; set; }
    public void SetSnackbarPresenter(SnackbarPresenter contentPresenter) { }
    public SnackbarPresenter? GetSnackbarPresenter() => null;
    public void Show(string title, string message, Yuandl.ThemeUI.Enums.ControlAppearance appearance, IconElement? icon, TimeSpan timeout) { }
}

[TestClass]
public class RawDebugViewModelTests
{
    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new AggregateException("STA thread execution failed", exception);
        }
    }

    [TestMethod]
    public void SendAsync_PureHexInput_NeverAppendsCrcOrModbusHeaders()
    {
        RunInSta(() =>
        {
            // Arrange
            var mockComm = new MockCommunicationService();
            var mockSnackbar = new MockSnackbarService();
            var vm = new RawDebugViewModel(mockComm, mockSnackbar)
            {
                SendEncoding = DataEncoding.Hex,
                SelectedDelimiter = EndingDelimiter.None,
                InputText = "01 03 00 00 00 02" // 6 字节原始数据
            };

            // Act
            vm.SendAsync().GetAwaiter().GetResult();

            // Assert
            Assert.IsNotNull(mockComm.LastSentBytes);
            // 关键约束检查：Raw 模式不得自动添加 CRC (C4 0B) 或任何多余字节！
            byte[] expected = [0x01, 0x03, 0x00, 0x00, 0x00, 0x02];
            CollectionAssert.AreEqual(expected, mockComm.LastSentBytes, "Raw 模式发送的数据必须与用户输入严格一致，不得自动加码！");

            Assert.AreEqual(1, vm.Messages.Count);
            Assert.IsTrue(vm.Messages[0].IsTx);
            Assert.AreEqual(6, vm.TotalTxBytes);
            Assert.AreEqual(1, vm.TotalTxPackets);
            Assert.AreEqual("01 03 00 00 00 02", vm.SendHistory[0]);
        });
    }

    [TestMethod]
    public void SendAsync_WithCrlfDelimiter_AppendsDelimiterOnly()
    {
        RunInSta(() =>
        {
            // Arrange
            var mockComm = new MockCommunicationService();
            var mockSnackbar = new MockSnackbarService();
            var vm = new RawDebugViewModel(mockComm, mockSnackbar)
            {
                SendEncoding = DataEncoding.Ascii,
                SelectedDelimiter = EndingDelimiter.CRLF,
                InputText = "AT+RESET"
            };

            // Act
            vm.SendAsync().GetAwaiter().GetResult();

            // Assert
            byte[] expected = [0x41, 0x54, 0x2B, 0x52, 0x45, 0x53, 0x45, 0x54, 0x0D, 0x0A];
            CollectionAssert.AreEqual(expected, mockComm.LastSentBytes);
            Assert.AreEqual(10, vm.TotalTxBytes);
        });
    }

    [TestMethod]
    public void Receive_WhenPaused_DoesNotAddMessages()
    {
        RunInSta(() =>
        {
            // Arrange
            var mockComm = new MockCommunicationService();
            var mockSnackbar = new MockSnackbarService();
            var vm = new RawDebugViewModel(mockComm, mockSnackbar)
            {
                IsPauseReceive = true
            };

            // Act
            mockComm.RaiseDataReceived([0x11, 0x22, 0x33]);

            // Assert
            Assert.AreEqual(0, vm.Messages.Count, "暂停接收时不得将数据添加到显示流");
        });
    }
}
