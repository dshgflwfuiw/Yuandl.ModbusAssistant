using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class RawDebugViewModel : ObservableObject, IDisposable
{
    private readonly CommunicationService _comm;
    private readonly object _loopSync = new();
    private CancellationTokenSource? _loopCts;
    private PeriodicTimer? _loopTimer;
    private Task? _loopTask;
    private long _txPackets;
    private long _txBytes;
    private long _rxPackets;
    private long _rxBytes;

    public RawDebugViewModel(CommunicationService comm)
    {
        _comm = comm;

        _comm.ClientsChanged += (_, clients) => RunOnUi(() =>
        {
            string? prevId = SelectedClient?.SessionId;
            TcpClients.Clear();
            TcpClients.Add(new TcpServerClientSession { SessionId = "", RemoteAddress = "广播全部客户端", RemotePort = 0 });
            foreach (var c in clients) TcpClients.Add(c);
            SelectedClient = TcpClients.FirstOrDefault(c => c.SessionId == prevId) ?? TcpClients[0];
        });

        _comm.StateChanged += (_, state) => RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsTcpServerMode));
            if (state != ChannelState.Connected && IsLoopSend)
                IsLoopSend = false;
        });

        _comm.DataReceived += (_, e) => RunOnUi(() =>
        {
            if (IsPauseReceive) return;
            _rxPackets++;
            _rxBytes += e.Bytes.Length;
            OnPropertyChanged(nameof(CountersText));

            Messages.Insert(0, new RawMessageItem
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Direction = "RX",
                RawBytes = e.Bytes,
                Content = ByteUtils.DecodeBytes(e.Bytes, ReceiveEncoding),
                SessionLabel = e.SessionLabel
            });
            while (Messages.Count > 500) Messages.RemoveAt(Messages.Count - 1);
        });

        SendCommand = new AsyncRelayCommand(SendRawAsync, ex => ErrorText = ex.Message);
        ClearCommand = new RelayCommand(() =>
        {
            Messages.Clear();
            _txPackets = 0;
            _txBytes = 0;
            _rxPackets = 0;
            _rxBytes = 0;
            OnPropertyChanged(nameof(CountersText));
        });
        ClearHistoryCommand = new RelayCommand(() => SendHistory.Clear());
        ExportLogCommand = new RelayCommand(ExportLog);
    }

    public IReadOnlyList<DataEncoding> Encodings { get; } = Enum.GetValues<DataEncoding>();
    public IReadOnlyList<EndingDelimiter> Delimiters { get; } = Enum.GetValues<EndingDelimiter>();
    public ObservableCollection<RawMessageItem> Messages { get; } = new();
    public ObservableCollection<TcpServerClientSession> TcpClients { get; } = new();
    public ObservableCollection<string> SendHistory { get; } = new();

    public bool IsTcpServerMode => _comm.ActiveType == CommunicationType.TcpServer;

    private TcpServerClientSession? _selectedClient;
    public TcpServerClientSession? SelectedClient { get => _selectedClient; set => SetProperty(ref _selectedClient, value); }

    private DataEncoding _sendEncoding = DataEncoding.Hex;
    public DataEncoding SendEncoding { get => _sendEncoding; set => SetProperty(ref _sendEncoding, value); }

    private DataEncoding _receiveEncoding = DataEncoding.Hex;
    public DataEncoding ReceiveEncoding
    {
        get => _receiveEncoding;
        set
        {
            if (SetProperty(ref _receiveEncoding, value))
                ReDecodeAllMessages();
        }
    }

    private EndingDelimiter _delimiter = EndingDelimiter.None;
    public EndingDelimiter Delimiter { get => _delimiter; set => SetProperty(ref _delimiter, value); }

    private bool _isPauseReceive;
    public bool IsPauseReceive { get => _isPauseReceive; set => SetProperty(ref _isPauseReceive, value); }

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }

    private bool _isLoopSend;
    public bool IsLoopSend
    {
        get => _isLoopSend;
        set
        {
            if (SetProperty(ref _isLoopSend, value))
            {
                if (value)
                    StartLoopSend();
                else
                    StopLoopSend("用户关闭循环发送");
            }
        }
    }

    private int _loopIntervalMs = 1000;
    public int LoopIntervalMs
    {
        get => _loopIntervalMs;
        set
        {
            if (SetProperty(ref _loopIntervalMs, Math.Clamp(value, 20, 60000)))
            {
                lock (_loopSync)
                {
                    if (_loopTimer != null)
                        _loopTimer.Period = TimeSpan.FromMilliseconds(_loopIntervalMs);
                }
            }
        }
    }

    private string? _selectedHistoryItem;
    public string? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set
        {
            if (SetProperty(ref _selectedHistoryItem, value) && !string.IsNullOrEmpty(value))
                PayloadText = value;
        }
    }

    private string _payloadText = "01 03 00 00 00 02 C4 0B";
    public string PayloadText { get => _payloadText; set => SetProperty(ref _payloadText, value); }

    private string _errorText = string.Empty;
    public string ErrorText { get => _errorText; set => SetProperty(ref _errorText, value); }

    public string CountersText => $"TX: {_txPackets}帧 / {_txBytes} B   |   RX: {_rxPackets}帧 / {_rxBytes} B";

    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ExportLogCommand { get; }

    private void StartLoopSend()
    {
        StopLoopSend("重启循环发送");
        lock (_loopSync)
        {
            var cts = new CancellationTokenSource();
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(LoopIntervalMs, 20, 60000)));
            _loopCts = cts;
            _loopTimer = timer;
            _loopTask = Task.Run(() => RunLoopSendAsync(timer, cts.Token), CancellationToken.None);
        }
        AppLogger.Info($"RawDebug 循环发送已启动 (间隔 {LoopIntervalMs} ms)");
    }

    private void StopLoopSend(string reason)
    {
        CancellationTokenSource? cts;
        PeriodicTimer? timer;
        lock (_loopSync)
        {
            cts = _loopCts;
            _loopCts = null;
            timer = _loopTimer;
            _loopTimer = null;
            _loopTask = null;
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
            AppLogger.Info($"RawDebug 循环发送停止: {reason}");
        }
        timer?.Dispose();
    }

    private async Task RunLoopSendAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (token.IsCancellationRequested)
                    break;
                await SendRawAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("RawDebug 循环发送后台任务已取消");
        }
        catch (Exception ex)
        {
            AppLogger.Error("RawDebug 循环发送后台任务异常终止", ex);
            RunOnUi(() =>
            {
                ErrorText = ex.Message;
                IsLoopSend = false;
            });
        }
    }

    private void ExportLog()
    {
        if (Messages.Count == 0)
        {
            ErrorText = "暂无可导出的收发记录";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|文本日志 (*.txt)|*.txt",
            FileName = $"raw-traffic-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        bool isTxt = Path.GetExtension(dlg.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        if (isTxt)
        {
            foreach (var m in Messages.Reverse())
                sb.AppendLine($"[{m.Timestamp}] [{m.Direction}] ({m.ByteLength} B) {m.SessionLabel} {m.Content}");
        }
        else
        {
            sb.AppendLine("Timestamp,Direction,ByteLength,Session,Content");
            foreach (var m in Messages.Reverse())
            {
                string escaped = (m.Content ?? string.Empty).Replace("\"", "\"\"");
                string session = (m.SessionLabel ?? string.Empty).Replace("\"", "\"\"");
                sb.AppendLine($"{m.Timestamp},{m.Direction},{m.ByteLength},\"{session}\",\"{escaped}\"");
            }
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        ErrorText = string.Empty;
    }

    private void ReDecodeAllMessages()
    {
        for (int i = 0; i < Messages.Count; i++)
        {
            var m = Messages[i];
            if (m.RawBytes.Length == 0) continue;
            Messages[i] = new RawMessageItem
            {
                Timestamp = m.Timestamp,
                Direction = m.Direction,
                RawBytes = m.RawBytes,
                Content = ByteUtils.DecodeBytes(m.RawBytes, m.Direction == "TX" ? SendEncoding : ReceiveEncoding),
                SessionLabel = m.SessionLabel
            };
        }
    }

    private Task SendRawAsync() => SendRawAsync(CancellationToken.None);

    private async Task SendRawAsync(CancellationToken cancellationToken)
    {
        RunOnUi(() => ErrorText = string.Empty);
        try
        {
            string payload = PayloadText;
            var encoding = SendEncoding;
            var delimiter = Delimiter;
            var bytes = ByteUtils.EncodeText(payload, encoding, delimiter);
            if (bytes.Length == 0)
            {
                RunOnUi(() =>
                {
                    ErrorText = "发送内容不能为空";
                    IsLoopSend = false;
                });
                return;
            }

            string? targetSession = IsTcpServerMode && !string.IsNullOrEmpty(SelectedClient?.SessionId)
                ? SelectedClient.SessionId
                : null;

            await _comm.SendAsync(bytes, targetSession, cancellationToken);

            RunOnUi(() =>
            {
                _txPackets++;
                _txBytes += bytes.Length;
                OnPropertyChanged(nameof(CountersText));

                Messages.Insert(0, new RawMessageItem
                {
                    Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                    Direction = "TX",
                    RawBytes = bytes,
                    Content = ByteUtils.DecodeBytes(bytes, encoding),
                    SessionLabel = targetSession != null ? $"-> {targetSession}" : (IsTcpServerMode ? "广播全部" : null)
                });
                while (Messages.Count > 500) Messages.RemoveAt(Messages.Count - 1);

                if (!SendHistory.Contains(payload))
                {
                    SendHistory.Insert(0, payload);
                    while (SendHistory.Count > 30) SendHistory.RemoveAt(SendHistory.Count - 1);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // expected on loop stop/cancel
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"RawDebug 发送失败: {ex.Message}");
            RunOnUi(() =>
            {
                ErrorText = ex.Message;
                IsLoopSend = false;
            });
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        StopLoopSend("ViewModel 销毁");
        _isLoopSend = false;
    }
}
