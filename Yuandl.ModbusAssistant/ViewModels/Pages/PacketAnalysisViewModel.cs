using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
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
    private const int MaxRttSamples = 2000;
    private readonly CommunicationService _comm;
    private readonly List<double> _rttHistory = new();
    private DateTime? _pendingTxTimestamp;
    private string _pendingTxDirectionKey = string.Empty;

    public PacketAnalysisViewModel(CommunicationService comm)
    {
        _comm = comm;
        CapturedPacketsView = CollectionViewSource.GetDefaultView(CapturedPackets);
        CapturedPacketsView.Filter = MatchesPacketFilter;
        _comm.TrafficCaptured += (_, e) => RunOnUi(() => HandleTraffic(e));

        ClearCommand = new RelayCommand(ClearAll);
        ParseManualCommand = new RelayCommand(ParseManualFrame);
        ExportLogCommand = new RelayCommand(ExportPackets);
    }

    public ObservableCollection<AnalyzedPacketEntry> CapturedPackets { get; } = new();
    public ICollectionView CapturedPacketsView { get; }
    public IReadOnlyList<string> DirectionFilters { get; } = new[] { "全部", "TX", "RX", "ERR" };
    public IReadOnlyList<string> FunctionCodeFilters { get; } = new[] { "全部", "01", "02", "03", "04", "05", "06", "0F", "10" };
    public IReadOnlyList<PacketCaptureTransportMode> CaptureModes { get; } = Enum.GetValues<PacketCaptureTransportMode>();
    public IReadOnlyList<ModbusTransport> ManualTransports { get; } = Enum.GetValues<ModbusTransport>();

    private bool _isCapturing = true;
    public bool IsCapturing { get => _isCapturing; set => SetProperty(ref _isCapturing, value); }

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }

    private PacketCaptureTransportMode _captureTransport = PacketCaptureTransportMode.Auto;
    public PacketCaptureTransportMode CaptureTransport { get => _captureTransport; set => SetProperty(ref _captureTransport, value); }

    private string _directionFilter = "全部";
    public string DirectionFilter
    {
        get => _directionFilter;
        set
        {
            if (SetProperty(ref _directionFilter, value)) CapturedPacketsView.Refresh();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) CapturedPacketsView.Refresh();
        }
    }

    private string _functionCodeFilter = "全部";
    public string FunctionCodeFilter
    {
        get => _functionCodeFilter;
        set
        {
            if (SetProperty(ref _functionCodeFilter, value)) CapturedPacketsView.Refresh();
        }
    }

    private int _totalPackets;
    public int TotalPackets { get => _totalPackets; private set => SetProperty(ref _totalPackets, value); }

    private int _errorPackets;
    public int ErrorPackets { get => _errorPackets; private set => SetProperty(ref _errorPackets, value); }

    private int _matchedPairs;
    public int MatchedPairs { get => _matchedPairs; private set => SetProperty(ref _matchedPairs, value); }

    private string _rttStatsSummary = "平均 — | 最小 — | 最大 —";
    public string RttStatsSummary { get => _rttStatsSummary; private set => SetProperty(ref _rttStatsSummary, value); }

    private ModbusTransport _manualTransport = ModbusTransport.Rtu;
    public ModbusTransport ManualTransport { get => _manualTransport; set => SetProperty(ref _manualTransport, value); }

    private string _manualHexInput = "01 03 00 00 00 02 C4 0B";
    public string ManualHexInput { get => _manualHexInput; set => SetProperty(ref _manualHexInput, value); }

    private string _manualResultText = "输入十六进制报文后点击“手动解析”。";
    public string ManualResultText { get => _manualResultText; private set => SetProperty(ref _manualResultText, value); }

    public RelayCommand ClearCommand { get; }
    public RelayCommand ParseManualCommand { get; }
    public RelayCommand ExportLogCommand { get; }

    private void ExportPackets()
    {
        var packets = CapturedPacketsView.Cast<AnalyzedPacketEntry>().ToList();
        if (packets.Count == 0)
        {
            ManualResultText = "暂无可导出的捕获报文";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|文本日志 (*.txt)|*.txt",
            FileName = $"modbus-packets-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        bool isTxt = Path.GetExtension(dlg.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        if (isTxt)
        {
            foreach (var p in packets.AsEnumerable().Reverse())
                sb.AppendLine($"[{p.Timestamp}] [{p.Direction}] [{p.Transport}] RTT={p.RttText} HEX={p.Hex} | {p.Result}");
        }
        else
        {
            sb.AppendLine("Timestamp,Direction,Transport,Rtt,Session,Hex,Result");
            foreach (var p in packets.AsEnumerable().Reverse())
            {
                string result = (p.Result ?? string.Empty).Replace("\"", "\"\"");
                string session = (p.SessionLabel ?? string.Empty).Replace("\"", "\"\"");
                sb.AppendLine($"\"{p.Timestamp}\",\"{p.Direction}\",\"{p.Transport}\",\"{p.RttText}\",\"{session}\",\"{p.Hex}\",\"{result}\"");
            }
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        ManualResultText = $"已导出 {packets.Count} 条分析报文: {dlg.FileName}";
    }

    private bool MatchesPacketFilter(object item)
    {
        if (item is not AnalyzedPacketEntry packet) return false;
        if (DirectionFilter != "全部" && !string.Equals(packet.Direction, DirectionFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (FunctionCodeFilter != "全部" && !packet.Result.Contains($"FC 0x{FunctionCodeFilter}", StringComparison.OrdinalIgnoreCase)) return false;
        var query = SearchText.Trim();
        return query.Length == 0 || packet.Hex.Contains(query, StringComparison.OrdinalIgnoreCase)
            || packet.Result.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (packet.SessionLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || packet.Transport.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

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
            while (_rttHistory.Count > MaxRttSamples)
                _rttHistory.RemoveAt(0);
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
