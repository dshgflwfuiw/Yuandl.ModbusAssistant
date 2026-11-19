using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

/// <summary>
/// Modbus 调试页面 ViewModel
/// 负责 01/02/03/04/05/06/0F/10 等标准功能码的报文交互与调试
/// </summary>
public partial class ModbusDebugViewModel : ViewModelBase
{
    private readonly HslModbusService? _modbus;
    [ObservableProperty]
    private byte slaveId = 1;

    [ObservableProperty]
    private string selectedFunctionCode = "03: 读取保持寄存器";

    [ObservableProperty] private ushort startAddress;
    [ObservableProperty] private ushort quantity = 1;
    [ObservableProperty] private string requestHex = string.Empty;
    [ObservableProperty] private string responseHex = string.Empty;
    [ObservableProperty] private string resultText = string.Empty;
    public ModbusTransport[] TransportOptions { get; } = Enum.GetValues<ModbusTransport>();
    public string[] FunctionCodeOptions { get; } = ["01: 读取线圈", "02: 读取离散输入", "03: 读取保持寄存器", "04: 读取输入寄存器", "05: 写单个线圈", "06: 写单个寄存器", "0F: 写多个线圈", "10: 写多个寄存器"];
    [ObservableProperty] private ModbusTransport selectedTransport = ModbusTransport.Rtu;
    [ObservableProperty] private ushort writeValue;
    [ObservableProperty] private string statusText = "就绪";

    public ModbusDebugViewModel(HslModbusService? modbus = null)
    {
        _modbus = modbus;
        Title = "Modbus 调试";
    }

    [RelayCommand]
    public async Task SendAsync()
    {
        try
        {
            BuildRequest();
            if (_modbus is null) { StatusText = "未配置 Hsl Modbus 服务"; return; }
            var response = await _modbus.ExchangeAsync(ByteUtils.HexToBytes(RequestHex));
            ResponseHex = ByteUtils.BytesToHex(response);
            ParseResponse();
            StatusText = "请求完成";
        }
        catch (Exception ex) { StatusText = $"请求失败：{ex.Message}"; }
    }

    [RelayCommand]
    public void BuildRequest()
    {
        var code = byte.Parse(SelectedFunctionCode[..2], System.Globalization.NumberStyles.HexNumber);
        RequestHex = ByteUtils.BytesToHex(code switch
        {
            5 or 6 => ModbusFrame.BuildWriteSingle(SlaveId, code, StartAddress, WriteValue, SelectedTransport),
            15 or 16 => ModbusFrame.BuildWriteMultiple(SlaveId, code, StartAddress, [WriteValue], SelectedTransport),
            _ => ModbusFrame.BuildRequest(SlaveId, code, StartAddress, Quantity, SelectedTransport)
        });
    }

    [RelayCommand]
    public void ParseResponse()
    {
        try { var response = ModbusFrame.ParseResponse(ByteUtils.HexToBytes(ResponseHex), SelectedTransport); ResultText = response.IsException ? $"异常码 0x{response.ExceptionCode:X2}" : $"功能码 0x{response.FunctionCode:X2}，寄存器数 {response.Registers.Length}"; }
        catch (Exception ex) { ResultText = ex.Message; }
    }
}
