using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class RawDebugViewModel : ObservableObject
{
    private readonly CommunicationService _comm;
    private readonly DispatcherTimer _loopTimer;
    private long _txPackets;
    private long _txBytes;
    private long _rxPackets;
    private long _rxBytes;

    public RawDebugViewModel(CommunicationService comm)
    {
        _comm = comm;
        _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _loopTimer.Tick += async (_, _) => await SendRawAsync();

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

        SendCommand = new AsyncRelayCommand(SendRawAsync);
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

    private bool _isLoopSend;
    public bool IsLoopSend
    {
        get => _isLoopSend;
        set
        {
            if (SetProperty(ref _isLoopSend, value))
            {
                if (value)
                {
                    _loopTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(LoopIntervalMs, 20, 60000));
                    _loopTimer.Start();
                }
                else
                {
                    _loopTimer.Stop();
                }
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
                _loopTimer.Interval = TimeSpan.FromMilliseconds(_loopIntervalMs);
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

    private async Task SendRawAsync()
    {
        ErrorText = string.Empty;
        try
        {
            var bytes = ByteUtils.EncodeText(PayloadText, SendEncoding, Delimiter);
            if (bytes.Length == 0)
            {
                ErrorText = "发送内容不能为空";
                IsLoopSend = false;
                return;
            }

            string? targetSession = IsTcpServerMode && !string.IsNullOrEmpty(SelectedClient?.SessionId)
                ? SelectedClient.SessionId
                : null;

            await _comm.SendAsync(bytes, targetSession);

            _txPackets++;
            _txBytes += bytes.Length;
            OnPropertyChanged(nameof(CountersText));

            Messages.Insert(0, new RawMessageItem
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Direction = "TX",
                RawBytes = bytes,
                Content = ByteUtils.DecodeBytes(bytes, SendEncoding),
                SessionLabel = targetSession != null ? $"-> {targetSession}" : (IsTcpServerMode ? "广播全部" : null)
            });
            while (Messages.Count > 500) Messages.RemoveAt(Messages.Count - 1);

            if (!SendHistory.Contains(PayloadText))
            {
                SendHistory.Insert(0, PayloadText);
                while (SendHistory.Count > 30) SendHistory.RemoveAt(SendHistory.Count - 1);
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            IsLoopSend = false;
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
