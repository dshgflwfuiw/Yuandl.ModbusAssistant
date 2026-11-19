using System.Collections.ObjectModel;
using System.Windows;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public enum PacketCaptureTransportMode
{
    Auto,
    Tcp,
    Rtu,
    Ascii
}

public sealed class PacketAnalysisViewModel : ObservableObject
{
    private readonly CommunicationService _comm;
    private readonly List<double> _rttHistory = new();
    private DateTime? _pendingTxTimestamp;
    private string _pendingTxDirectionKey = string.Empty;

    public PacketAnalysisViewModel(CommunicationService comm)
    {
        _comm = comm;
        _comm.TrafficCaptured += (_, e) => RunOnUi(() => HandleTraffic(e));

        ClearCommand = new RelayCommand(ClearAll);
        ParseManualCommand = new RelayCommand(ParseManualFrame);
    }

    public ObservableCollection<AnalyzedPacketEntry> CapturedPackets { get; } = new();
    public IReadOnlyList<PacketCaptureTransportMode> CaptureModes { get; } = Enum.GetValues<PacketCaptureTransportMode>();
    public IReadOnlyList<ModbusTransport> ManualTransports { get; } = Enum.GetValues<ModbusTransport>();

    private bool _isCapturing = true;
    public bool IsCapturing { get => _isCapturing; set => SetProperty(ref _isCapturing, value); }

    private PacketCaptureTransportMode _captureTransport = PacketCaptureTransportMode.Auto;
    public PacketCaptureTransportMode CaptureTransport { get => _captureTransport; set => SetProperty(ref _captureTransport, value); }

    private int _totalPackets;
    public int TotalPackets { get => _totalPackets; private set => SetProperty(ref _totalPackets, value); }

    private int _errorPackets;
    public int ErrorPackets { get => _errorPackets; private set => SetProperty(ref _errorPackets, value); }

    private int _matchedPairs;
    public int MatchedPairs { get => _matchedPairs; private set => SetProperty(ref _matchedPairs, value); }

    private string _rttStatsSummary = "平均 — | 最小 — | 最大 —";
    public string RttStatsSummary { get => _rttStatsSummary; private set => SetProperty(ref _rttStatsSummary, value); }

    // Manual hex analyzer
    private ModbusTransport _manualTransport = ModbusTransport.Rtu;
    public ModbusTransport ManualTransport { get => _manualTransport; set => SetProperty(ref _manualTransport, value); }

    private string _manualHexInput = "01 03 00 00 00 02 C4 0B";
    public string ManualHexInput { get => _manualHexInput; set => SetProperty(ref _manualHexInput, value); }

    private string _manualResultText = "输入十六进制报文后点击“手动解析”。";
    public string ManualResultText { get => _manualResultText; private set => SetProperty(ref _manualResultText, value); }

    public RelayCommand ClearCommand { get; }
    public RelayCommand ParseManualCommand { get; }

    private void HandleTraffic(TrafficEventArgs e)
    {
        if (!IsCapturing) return;

        var transport = DetectTransport(e.Bytes, e.ChannelType);
        double? rttMs = null;

        if (e.Direction == "TX")
        {
            _pendingTxTimestamp = e.Timestamp;
            _pendingTxDirectionKey = e.SessionId ?? "default";
        }
        else if (e.Direction == "RX" && _pendingTxTimestamp.HasValue && _pendingTxDirectionKey == (e.SessionId ?? "default"))
        {
            rttMs = Math.Max(0, (e.Timestamp - _pendingTxTimestamp.Value).TotalMilliseconds);
            _pendingTxTimestamp = null;
            _rttHistory.Add(rttMs.Value);
            MatchedPairs++;
            UpdateRttStats();
        }

        var (summary, isError) = TryAnalyzeFrame(e.Bytes, transport, e.Direction);
        if (isError) ErrorPackets++;
        TotalPackets++;

        CapturedPackets.Insert(0, new AnalyzedPacketEntry
        {
            Timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
            Direction = e.Direction,
            Transport = transport,
            Hex = e.Hex,
            Result = summary,
            RttMs = rttMs,
            SessionLabel = e.SessionLabel
        });

        while (CapturedPackets.Count > 500)
            CapturedPackets.RemoveAt(CapturedPackets.Count - 1);
    }

    private ModbusTransport DetectTransport(byte[] bytes, CommunicationType? channelType)
    {
        if (CaptureTransport != PacketCaptureTransportMode.Auto)
        {
            return CaptureTransport switch
            {
                PacketCaptureTransportMode.Tcp => ModbusTransport.Tcp,
                PacketCaptureTransportMode.Ascii => ModbusTransport.Ascii,
                _ => ModbusTransport.Rtu
            };
        }

        if (bytes.Length >= 7 && bytes[0] == (byte)':' && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n')
            return ModbusTransport.Ascii;

        if (channelType is CommunicationType.TcpClient or CommunicationType.TcpServer && bytes.Length >= 8 && ((bytes[2] << 8) | bytes[3]) == 0)
            return ModbusTransport.Tcp;

        return ModbusTransport.Rtu;
    }

    private static (string Summary, bool IsError) TryAnalyzeFrame(byte[] bytes, ModbusTransport transport, string direction)
    {
        try
        {
            if (direction == "TX")
            {
                var req = ModbusFrame.ParseRequest(bytes, transport);
                string txTag = req.TransactionId.HasValue ? $"TxId={req.TransactionId} | " : string.Empty;
                return ($"{txTag}站号 {req.UnitId} | FC 0x{req.FunctionCode:X2} | 地址 {req.Address} | 数量 {req.Quantity}", false);
            }

            var resp = ModbusFrame.ParseResponse(bytes, transport);
            if (resp.IsException)
                return ($"异常响应 FC 0x{resp.FunctionCode:X2}: {ModbusException.Describe(resp.ExceptionCode ?? 0)}", true);

            return ($"正常响应 站号 {resp.UnitId} | FC 0x{resp.FunctionCode:X2} | 载荷 {resp.Payload.Length} B", false);
        }
        catch (Exception ex)
        {
            return ($"非标准Modbus/校验失败: {ex.Message}", false);
        }
    }

    private void ParseManualFrame()
    {
        try
        {
            var bytes = ByteUtils.HexToBytes(ManualHexInput);
            if (bytes.Length == 0)
            {
                ManualResultText = "报文为空";
                return;
            }

            try
            {
                var req = ModbusFrame.ParseRequest(bytes, ManualTransport);
                ManualResultText = $"[请求帧] 站号={req.UnitId}, 功能码=0x{req.FunctionCode:X2}, 起始地址={req.Address}, 数量={req.Quantity}" +
                                   (req.Values.Length > 0 ? $", 值=[{string.Join(", ", req.Values)}]" : string.Empty);
                return;
            }
            catch { /* try as response */ }

            var resp = ModbusFrame.ParseResponse(bytes, ManualTransport);
            ManualResultText = resp.IsException
                ? $"[异常响应] 站号={resp.UnitId}, 功能码=0x{resp.FunctionCode:X2}, 异常={ModbusException.Describe(resp.ExceptionCode ?? 0)}"
                : $"[正常响应] 站号={resp.UnitId}, 功能码=0x{resp.FunctionCode:X2}, 载荷={ByteUtils.BytesToHex(resp.Payload)}, 寄存器=[{string.Join(", ", resp.Registers)}]";
        }
        catch (Exception ex)
        {
            ManualResultText = $"解析失败: {ex.Message}";
        }
    }

    private void UpdateRttStats()
    {
        if (_rttHistory.Count == 0)
        {
            RttStatsSummary = "平均 — | 最小 — | 最大 —";
            return;
        }
        double avg = _rttHistory.Average();
        double min = _rttHistory.Min();
        double max = _rttHistory.Max();
        RttStatsSummary = $"平均 {avg:0.#} ms | 最小 {min:0.#} ms | 最大 {max:0.#} ms";
    }

    private void ClearAll()
    {
        CapturedPackets.Clear();
        _rttHistory.Clear();
        _pendingTxTimestamp = null;
        TotalPackets = 0;
        ErrorPackets = 0;
        MatchedPairs = 0;
        UpdateRttStats();
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
