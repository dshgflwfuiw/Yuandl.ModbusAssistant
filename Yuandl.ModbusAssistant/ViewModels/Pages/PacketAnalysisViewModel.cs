using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Protocol.Modbus;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

/// <summary>
/// 报文分析页面 ViewModel
/// 负责请求响应关联匹配、CRC校验、异常响应码深度解析及通讯时延统计
/// </summary>
public partial class PacketAnalysisViewModel : ViewModelBase
{
    public sealed record AnalysisEntry(DateTime Timestamp, string Direction, string Hex, string Result);
    [ObservableProperty] private string inputHex = string.Empty;
    [ObservableProperty] private string analysisResult = string.Empty;
    [ObservableProperty] private ObservableCollection<AnalysisEntry> entries = [];
    public ModbusTransport[] TransportOptions { get; } = Enum.GetValues<ModbusTransport>();
    [ObservableProperty] private ModbusTransport selectedTransport = ModbusTransport.Rtu;
    [ObservableProperty]
    private int totalPackets;

    [ObservableProperty]
    private int errorPackets;

    public PacketAnalysisViewModel()
    {
        Title = "报文分析";
    }

    [RelayCommand]
    public void Analyze()
    {
        try
        {
            var bytes = ByteUtils.HexToBytes(InputHex);
            var response = ModbusFrame.ParseResponse(bytes, SelectedTransport);
            var result = response.IsException ? $"异常码 0x{response.ExceptionCode:X2}: {ModbusException.Describe(response.ExceptionCode ?? 0)}" : $"地址 {response.UnitId}，功能码 0x{response.FunctionCode:X2}，寄存器 {response.Registers.Length}，CRC/LRC 正确";
            AnalysisResult = result; TotalPackets++; Entries.Insert(0, new AnalysisEntry(DateTime.Now, "RX", ByteUtils.BytesToHex(bytes), result));
        }
        catch (Exception ex) { AnalysisResult = ex.Message; ErrorPackets++; Entries.Insert(0, new AnalysisEntry(DateTime.Now, "ERR", InputHex, ex.Message)); }
    }

    [RelayCommand] public void Clear() { Entries.Clear(); TotalPackets = 0; ErrorPackets = 0; AnalysisResult = string.Empty; }
}
