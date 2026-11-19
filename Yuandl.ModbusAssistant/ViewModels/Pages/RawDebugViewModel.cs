using System.Collections.ObjectModel;
using System.Windows;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class RawDebugViewModel : ObservableObject
{
    private readonly CommunicationService _comm;

    public RawDebugViewModel(CommunicationService comm)
    {
        _comm = comm;

        _comm.ClientsChanged += (_, clients) => RunOnUi(() =>
        {
            TcpClients.Clear();
            TcpClients.Add(new TcpServerClientSession { SessionId = "", RemoteAddress = "广播全部客户端", RemotePort = 0 });
            foreach (var c in clients) TcpClients.Add(c);
            if (SelectedClient == null && TcpClients.Count > 0)
                SelectedClient = TcpClients[0];
        });

        _comm.StateChanged += (_, _) => RunOnUi(() => OnPropertyChanged(nameof(IsTcpServerMode)));

        _comm.DataReceived += (_, e) => RunOnUi(() =>
        {
            Messages.Insert(0, new RawMessageItem
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Direction = "RX",
                Content = ByteUtils.DecodeBytes(e.Bytes, ReceiveEncoding),
                SessionLabel = e.SessionLabel
            });
            while (Messages.Count > 500) Messages.RemoveAt(Messages.Count - 1);
        });

        SendCommand = new AsyncRelayCommand(SendRawAsync);
        ClearCommand = new RelayCommand(() => Messages.Clear());
    }

    public IReadOnlyList<DataEncoding> Encodings { get; } = Enum.GetValues<DataEncoding>();
    public IReadOnlyList<EndingDelimiter> Delimiters { get; } = Enum.GetValues<EndingDelimiter>();
    public ObservableCollection<RawMessageItem> Messages { get; } = new();
    public ObservableCollection<TcpServerClientSession> TcpClients { get; } = new();

    public bool IsTcpServerMode => _comm.ActiveType == CommunicationType.TcpServer;

    private TcpServerClientSession? _selectedClient;
    public TcpServerClientSession? SelectedClient
    {
        get => _selectedClient;
        set => SetProperty(ref _selectedClient, value);
    }

    private DataEncoding _sendEncoding = DataEncoding.Hex;
    public DataEncoding SendEncoding { get => _sendEncoding; set => SetProperty(ref _sendEncoding, value); }

    private DataEncoding _receiveEncoding = DataEncoding.Hex;
    public DataEncoding ReceiveEncoding { get => _receiveEncoding; set => SetProperty(ref _receiveEncoding, value); }

    private EndingDelimiter _delimiter = EndingDelimiter.None;
    public EndingDelimiter Delimiter { get => _delimiter; set => SetProperty(ref _delimiter, value); }

    private string _payloadText = "01 03 00 00 00 02";
    public string PayloadText { get => _payloadText; set => SetProperty(ref _payloadText, value); }

    private string _errorText = string.Empty;
    public string ErrorText { get => _errorText; set => SetProperty(ref _errorText, value); }

    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand ClearCommand { get; }

    private async Task SendRawAsync()
    {
        ErrorText = string.Empty;
        try
        {
            var bytes = ByteUtils.EncodeText(PayloadText, SendEncoding, Delimiter);
            if (bytes.Length == 0)
            {
                ErrorText = "发送内容不能为空";
                return;
            }

            string? targetSession = IsTcpServerMode && !string.IsNullOrEmpty(SelectedClient?.SessionId)
                ? SelectedClient.SessionId
                : null;

            await _comm.SendAsync(bytes, targetSession);

            Messages.Insert(0, new RawMessageItem
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Direction = "TX",
                Content = ByteUtils.DecodeBytes(bytes, SendEncoding),
                SessionLabel = targetSession != null ? $"-> {targetSession}" : (IsTcpServerMode ? "广播全部" : null)
            });
            while (Messages.Count > 500) Messages.RemoveAt(Messages.Count - 1);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
