using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yuandl.ModbusAssistant.Communication.Events;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models.Raw;
using Yuandl.ModbusAssistant.Services;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Enums;
using Yuandl.ThemeUI.Extensions;
using Yuandl.ThemeUI.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

/// <summary>
/// 无协议调试 (Raw 模式) ViewModel
/// 纯净收发原始字节流，支持 HEX/ASCII/UTF-8，严禁任何 Modbus 封装或 CRC 计算
/// </summary>
public partial class RawDebugViewModel : ViewModelBase, IDisposable
{
    private readonly ICommunicationService _commService;
    private readonly ISnackbarService _snackbarService;
    private CancellationTokenSource? _loopSendCts;
    private Task? _loopSendTask;

    // 接收报文列表最大保留行数，防止高频刷屏占用过多内存导致 WPF 渲染卡顿
    private const int MaxMessageCount = 1000;

    [ObservableProperty]
    private DataEncoding sendEncoding = DataEncoding.Hex;

    [ObservableProperty]
    private DataEncoding receiveEncoding = DataEncoding.Hex;

    [ObservableProperty]
    private EndingDelimiter selectedDelimiter = EndingDelimiter.None;

    [ObservableProperty]
    private string inputText = "01 03 00 00 00 02 C4 0B";

    [ObservableProperty]
    private bool isPauseReceive = false;

    [ObservableProperty]
    private bool isAutoScroll = true;

    [ObservableProperty]
    private bool showTimestamp = true;

    [ObservableProperty]
    private bool isLoopSend = false;

    [ObservableProperty]
    private int loopIntervalMs = 1000;

    [ObservableProperty]
    private long totalTxBytes;

    [ObservableProperty]
    private long totalRxBytes;

    [ObservableProperty]
    private long totalTxPackets;

    [ObservableProperty]
    private long totalRxPackets;

    [ObservableProperty]
    private ObservableCollection<RawMessageItem> messages = [];

    [ObservableProperty]
    private ObservableCollection<string> sendHistory = [];

    [ObservableProperty]
    private string? selectedHistoryItem;

    public DataEncoding[] EncodingOptions { get; } = [DataEncoding.Hex, DataEncoding.Ascii, DataEncoding.Utf8];
    public EndingDelimiter[] DelimiterOptions { get; } = [EndingDelimiter.None, EndingDelimiter.CR, EndingDelimiter.LF, EndingDelimiter.CRLF];

    public RawDebugViewModel(ICommunicationService commService, ISnackbarService snackbarService)
    {
        _commService = commService ?? throw new ArgumentNullException(nameof(commService));
        _snackbarService = snackbarService ?? throw new ArgumentNullException(nameof(snackbarService));

        Title = "无协议调试";

        _commService.DataReceived += OnDataReceived;
        _commService.StateChanged += OnChannelStateChanged;
    }

    partial void OnSelectedHistoryItemChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            InputText = value;
        }
    }

    partial void OnReceiveEncodingChanged(DataEncoding value)
    {
        // 当用户切换接收显示编码时，重新投影更新现有消息文本
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var updated = new ObservableCollection<RawMessageItem>();
            foreach (var msg in Messages)
            {
                updated.Add(new RawMessageItem(msg.IsTx, msg.RawBytes, value, msg.Timestamp));
            }
            Messages = updated;
        });
    }

    [RelayCommand]
    public async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            _snackbarService.Show("发送提示", "请输入待发送的数据内容", ControlAppearance.Info);
            return;
        }

        if (!_commService.IsConnected)
        {
            _snackbarService.Show("无法发送", "当前尚未建立通讯连接，请先前往 [通讯连接] 页面打开通道", ControlAppearance.Warning);
            return;
        }

        byte[] rawBytes;
        try
        {
            // 严禁在此处附加任何 Modbus 帧头、地址码或 CRC 校验！完全按用户输入进行纯净转换
            rawBytes = ByteUtils.EncodeInput(InputText, SendEncoding, SelectedDelimiter);
        }
        catch (Exception ex)
        {
            _snackbarService.Show("数据格式错误", ex.Message, ControlAppearance.Error);
            return;
        }

        if (rawBytes.Length == 0)
        {
            return;
        }

        try
        {
            await _commService.SendAsync(rawBytes);

            // 更新发送统计与历史
            TotalTxBytes += rawBytes.Length;
            TotalTxPackets++;

            AppendMessage(new RawMessageItem(true, rawBytes, SendEncoding));

            if (!SendHistory.Contains(InputText))
            {
                SendHistory.Insert(0, InputText);
                if (SendHistory.Count > 30)
                {
                    SendHistory.RemoveAt(SendHistory.Count - 1);
                }
            }
        }
        catch (Exception ex)
        {
            _snackbarService.Show("发送失败", ex.Message, ControlAppearance.Error);
        }
    }

    [RelayCommand]
    public void ToggleLoopSend()
    {
        if (IsLoopSend)
        {
            StartLoopSend();
        }
        else
        {
            StopLoopSend();
        }
    }

    private void StartLoopSend()
    {
        StopLoopSend();

        if (LoopIntervalMs < 10)
        {
            LoopIntervalMs = 10;
        }

        _loopSendCts = new CancellationTokenSource();
        var token = _loopSendCts.Token;

        _loopSendTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (IsLoopSend && _commService.IsConnected)
                    {
                        await SendAsync();
                    }
                    else if (!_commService.IsConnected)
                    {
                        IsLoopSend = false;
                        StopLoopSend();
                    }
                });

                try
                {
                    await Task.Delay(LoopIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void StopLoopSend()
    {
        try
        {
            _loopSendCts?.Cancel();
            _loopSendCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _loopSendCts = null;
            _loopSendTask = null;
        }
    }

    [RelayCommand]
    public void ClearMessages()
    {
        Messages.Clear();
        TotalTxBytes = 0;
        TotalRxBytes = 0;
        TotalTxPackets = 0;
        TotalRxPackets = 0;
    }

    [RelayCommand]
    public void ClearHistory()
    {
        SendHistory.Clear();
    }

    private void OnDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (IsPauseReceive)
        {
            return;
        }

        byte[] receivedBytes = e.Data.ToArray();
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            TotalRxBytes += receivedBytes.Length;
            TotalRxPackets++;
            AppendMessage(new RawMessageItem(false, receivedBytes, ReceiveEncoding, e.Timestamp));
        });
    }

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e)
    {
        if (e.NewState != ChannelState.Connected && IsLoopSend)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsLoopSend = false;
                StopLoopSend();
            });
        }
    }

    private void AppendMessage(RawMessageItem item)
    {
        if (Messages.Count >= MaxMessageCount)
        {
            Messages.RemoveAt(0);
        }
        Messages.Add(item);
    }

    public void Dispose()
    {
        StopLoopSend();
        _commService.DataReceived -= OnDataReceived;
        _commService.StateChanged -= OnChannelStateChanged;
        GC.SuppressFinalize(this);
    }
}
