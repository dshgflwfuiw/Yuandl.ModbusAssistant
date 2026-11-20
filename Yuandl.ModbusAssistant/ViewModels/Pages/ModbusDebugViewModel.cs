using System.Collections.ObjectModel;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Core.Utils;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public sealed class ModbusFunctionOption
{
    public byte Code { get; init; }
    public string Label { get; init; } = string.Empty;
    public override string ToString() => Label;
}

public sealed class ModbusDecodedItem
{
    public int Address { get; init; }
    public string HexValue { get; init; } = string.Empty;
    public string DecimalValue { get; init; } = string.Empty;
    public string SignedOrBool { get; init; } = string.Empty;
}

public sealed class ModbusDebugViewModel : ObservableObject
{
    private readonly CommunicationService _comm;
    private readonly SettingsViewModel _settings;
    private ushort _transactionId = 1;

    public ModbusDebugViewModel(CommunicationService comm, SettingsViewModel settings)
    {
        _comm = comm;
        _settings = settings;
        TimeoutMs = settings.DefaultTimeoutMs;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.DefaultTimeoutMs))
                TimeoutMs = settings.DefaultTimeoutMs;
        };

        Functions = new[]
        {
            new ModbusFunctionOption { Code = 1, Label = "01 (0x01) - 读线圈" },
            new ModbusFunctionOption { Code = 2, Label = "02 (0x02) - 读离散输入" },
            new ModbusFunctionOption { Code = 3, Label = "03 (0x03) - 读保持寄存器" },
            new ModbusFunctionOption { Code = 4, Label = "04 (0x04) - 读输入寄存器" },
            new ModbusFunctionOption { Code = 5, Label = "05 (0x05) - 写单个线圈" },
            new ModbusFunctionOption { Code = 6, Label = "06 (0x06) - 写单个寄存器" },
            new ModbusFunctionOption { Code = 15, Label = "0F (0x0F) - 写多个线圈" },
            new ModbusFunctionOption { Code = 16, Label = "10 (0x10) - 写多个寄存器" }
        };
        _selectedFunction = Functions[2];

        BuildPreviewCommand = new RelayCommand(() =>
        {
            BuildPreview();
            SummaryText = "请求报文已生成";
        });
        ExecuteCommand = new AsyncRelayCommand(ExecuteModbusAsync);
        ParseManualResponseCommand = new RelayCommand(() => DecodeResponseText(ResponseHex));
        BuildPreview();
    }

    public IReadOnlyList<ModbusTransport> Transports { get; } = Enum.GetValues<ModbusTransport>();
    public IReadOnlyList<ModbusFunctionOption> Functions { get; }
    public ObservableCollection<ModbusDecodedItem> DecodedItems { get; } = new();

    private ModbusTransport _transport = ModbusTransport.Tcp;
    public ModbusTransport Transport
    {
        get => _transport;
        set { if (SetProperty(ref _transport, value)) BuildPreview(); }
    }

    private byte _unitId = 1;
    public byte UnitId
    {
        get => _unitId;
        set { if (SetProperty(ref _unitId, value)) BuildPreview(); }
    }

    private ModbusFunctionOption _selectedFunction;
    public ModbusFunctionOption SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (SetProperty(ref _selectedFunction, value))
            {
                if (_selectedFunction.Code == 15 && BatchValuesText == "100, 200, 0x012C")
                    BatchValuesText = "true, false, 1, 0";
                else if (_selectedFunction.Code == 16 && BatchValuesText == "true, false, 1, 0")
                    BatchValuesText = "100, 200, 0x012C";

                OnPropertyChanged(nameof(IsBatchWrite));
                OnPropertyChanged(nameof(IsReadOrSingle));
                OnPropertyChanged(nameof(QuantityOrValueLabel));
                OnPropertyChanged(nameof(BatchListHint));
                BuildPreview();
            }
        }
    }

    private ushort _address;
    public ushort Address
    {
        get => _address;
        set { if (SetProperty(ref _address, value)) BuildPreview(); }
    }

    private ushort _quantityOrValue = 10;
    public ushort QuantityOrValue
    {
        get => _quantityOrValue;
        set { if (SetProperty(ref _quantityOrValue, value)) BuildPreview(); }
    }

    private string _batchValuesText = "100, 200, 0x012C";
    public string BatchValuesText
    {
        get => _batchValuesText;
        set { if (SetProperty(ref _batchValuesText, value)) BuildPreview(); }
    }

    private string _batchParsedStatus = "已解析 3 项";
    public string BatchParsedStatus { get => _batchParsedStatus; private set => SetProperty(ref _batchParsedStatus, value); }

    private bool _hasBatchError;
    public bool HasBatchError { get => _hasBatchError; private set => SetProperty(ref _hasBatchError, value); }

    private int _timeoutMs = 1000;
    public int TimeoutMs { get => _timeoutMs; set => SetProperty(ref _timeoutMs, Math.Clamp(value, 100, 30000)); }

    private string _requestPreviewHex = string.Empty;
    public string RequestPreviewHex { get => _requestPreviewHex; set => SetProperty(ref _requestPreviewHex, value); }

    private string _responseHex = string.Empty;
    public string ResponseHex { get => _responseHex; set => SetProperty(ref _responseHex, value); }

    private string _summaryText = "就绪";
    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }

    public bool IsBatchWrite => SelectedFunction.Code is 15 or 16;
    public bool IsReadOrSingle => !IsBatchWrite;
    public string QuantityOrValueLabel => SelectedFunction.Code switch
    {
        5 => "线圈值 (65280=ON, 0=OFF)",
        6 => "写入寄存器值 (0~65535)",
        _ => "数量 (Quantity)"
    };
    public string BatchListHint => SelectedFunction.Code == 15
        ? "批量写入线圈 (0F)：支持 true/false/1/0，逗号/空格/换行分隔，最多 1968 项"
        : "批量写入寄存器 (10)：支持十进制 0~65535 或 0x0000~0xFFFF，最多 123 项";

    public RelayCommand BuildPreviewCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public RelayCommand ParseManualResponseCommand { get; }

    private byte[] BuildFrameForTxId(ushort txId)
    {
        byte fn = SelectedFunction.Code;
        if (fn is 15 or 16)
        {
            var vals = ModbusFrame.ParseMultiValues(BatchValuesText, isCoils: fn == 15);
            BatchParsedStatus = $"已解析 {vals.Length} 项";
            HasBatchError = false;
            return ModbusFrame.BuildWriteMultiple(UnitId, fn, Address, vals, Transport, txId);
        }

        return fn switch
        {
            5 or 6 => ModbusFrame.BuildWriteSingle(UnitId, fn, Address, QuantityOrValue, Transport, txId),
            _ => ModbusFrame.BuildRequest(UnitId, fn, Address, QuantityOrValue, Transport, txId)
        };
    }

    private void BuildPreview()
    {
        try
        {
            var bytes = BuildFrameForTxId(_transactionId);
            RequestPreviewHex = ByteUtils.BytesToHex(bytes);
        }
        catch (Exception ex)
        {
            HasBatchError = IsBatchWrite;
            BatchParsedStatus = ex.Message;
            RequestPreviewHex = $"参数错误: {ex.Message}";
        }
    }

    private async Task ExecuteModbusAsync()
    {
        DecodedItems.Clear();
        ResponseHex = string.Empty;
        try
        {
            byte fn = SelectedFunction.Code;
            if (UnitId == 0 && fn is 1 or 2 or 3 or 4)
            {
                SummaryText = "参数提示：Modbus 广播地址 (站号 0) 仅支持写入功能码 (05/06/0F/10)";
                return;
            }

            ushort txId = Transport == ModbusTransport.Tcp ? _transactionId++ : (ushort)1;
            if (_transactionId == 0) _transactionId = 1;

            var frame = BuildFrameForTxId(txId);
            RequestPreviewHex = ByteUtils.BytesToHex(frame);

            var respBytes = await _comm.ExchangeModbusAsync(frame, Transport, TimeoutMs, _settings.DefaultRetryTimes);
            if (UnitId == 0 && respBytes.Length == 0)
            {
                ResponseHex = "(广播请求无从站响应)";
                SummaryText = $"已发送广播写入请求 · 站号 0 · FC 0x{fn:X2} · 起始地址 {Address}（从站按协议静默执行）";
                return;
            }

            ResponseHex = ByteUtils.BytesToHex(respBytes);
            DecodeResponseBytes(respBytes);
        }
        catch (Exception ex)
        {
            SummaryText = $"执行失败: {ex.Message}";
        }
    }

    private void DecodeResponseText(string hexText)
    {
        DecodedItems.Clear();
        try
        {
            var bytes = ByteUtils.HexToBytes(hexText);
            if (bytes.Length == 0)
            {
                SummaryText = "请输入待解析的响应 HEX 报文";
                return;
            }
            DecodeResponseBytes(bytes);
        }
        catch (Exception ex)
        {
            SummaryText = $"响应解析失败: {ex.Message}";
        }
    }

    private void DecodeResponseBytes(byte[] respBytes)
    {
        var parsed = ModbusFrame.ParseResponse(respBytes, Transport);
        if (parsed.IsException)
        {
            SummaryText = $"Modbus 异常响应 (0x{parsed.FunctionCode:X2}): {ModbusException.Describe(parsed.ExceptionCode ?? 0)}";
            return;
        }

        if (parsed.FunctionCode is 1 or 2)
        {
            var bits = ModbusFrame.ExtractBits(parsed.Payload, QuantityOrValue);
            for (int i = 0; i < bits.Length; i++)
            {
                DecodedItems.Add(new ModbusDecodedItem
                {
                    Address = Address + i,
                    HexValue = bits[i] ? "0x01" : "0x00",
                    DecimalValue = bits[i] ? "1" : "0",
                    SignedOrBool = bits[i] ? "ON (True)" : "OFF (False)"
                });
            }
            SummaryText = $"成功读取 {bits.Length} 个状态位 (FC 0x{parsed.FunctionCode:X2})";
        }
        else if (parsed.FunctionCode is 3 or 4)
        {
            for (int i = 0; i < parsed.Registers.Length; i++)
            {
                ushort val = parsed.Registers[i];
                DecodedItems.Add(new ModbusDecodedItem
                {
                    Address = Address + i,
                    HexValue = $"0x{val:X4}",
                    DecimalValue = val.ToString(),
                    SignedOrBool = ((short)val).ToString()
                });
            }
            SummaryText = $"成功读取 {parsed.Registers.Length} 个寄存器 (FC 0x{parsed.FunctionCode:X2})";
        }
        else if (parsed.FunctionCode is 15 or 16)
        {
            int addr = parsed.Payload.Length >= 4 ? (parsed.Payload[0] << 8) | parsed.Payload[1] : Address;
            int cnt = parsed.Payload.Length >= 4 ? (parsed.Payload[2] << 8) | parsed.Payload[3] : 0;
            SummaryText = $"批量写入成功 · FC 0x{parsed.FunctionCode:X2} · 起始地址 {addr} · 写入数量 {cnt}";
        }
        else
        {
            SummaryText = $"写入成功 (功能码 0x{parsed.FunctionCode:X2}，起始地址 {Address})";
        }
    }
}
