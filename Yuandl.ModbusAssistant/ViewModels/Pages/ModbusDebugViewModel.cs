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
    private ushort _transactionId = 1;

    public ModbusDebugViewModel(CommunicationService comm)
    {
        _comm = comm;

        Functions = new[]
        {
            new ModbusFunctionOption { Code = 1, Label = "01 - 读线圈 (Read Coils)" },
            new ModbusFunctionOption { Code = 2, Label = "02 - 读离散输入 (Read Discrete Inputs)" },
            new ModbusFunctionOption { Code = 3, Label = "03 - 读保持寄存器 (Read Holding Registers)" },
            new ModbusFunctionOption { Code = 4, Label = "04 - 读输入寄存器 (Read Input Registers)" },
            new ModbusFunctionOption { Code = 5, Label = "05 - 写单线圈 (Write Single Coil)" },
            new ModbusFunctionOption { Code = 6, Label = "06 - 写单寄存器 (Write Single Register)" },
            new ModbusFunctionOption { Code = 15, Label = "0F (15) - 写多个线圈 (Write Multiple Coils)" },
            new ModbusFunctionOption { Code = 16, Label = "10 (16) - 写多个寄存器 (Write Multiple Registers)" }
        };
        _selectedFunction = Functions[2]; // 03 default

        ExecuteCommand = new AsyncRelayCommand(ExecuteModbusAsync);
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
                OnPropertyChanged(nameof(IsBatchWrite));
                OnPropertyChanged(nameof(IsReadOrSingle));
                OnPropertyChanged(nameof(QuantityOrValueLabel));
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

    private string _batchValuesText = "100, 200, 300";
    public string BatchValuesText
    {
        get => _batchValuesText;
        set { if (SetProperty(ref _batchValuesText, value)) BuildPreview(); }
    }

    private int _timeoutMs = 1000;
    public int TimeoutMs { get => _timeoutMs; set => SetProperty(ref _timeoutMs, value); }

    private string _requestPreviewHex = string.Empty;
    public string RequestPreviewHex { get => _requestPreviewHex; private set => SetProperty(ref _requestPreviewHex, value); }

    private string _responseHex = string.Empty;
    public string ResponseHex { get => _responseHex; private set => SetProperty(ref _responseHex, value); }

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

    public AsyncRelayCommand ExecuteCommand { get; }

    private byte[] BuildFrameForTxId(ushort txId)
    {
        byte fn = SelectedFunction.Code;
        return fn switch
        {
            5 or 6 => ModbusFrame.BuildWriteSingle(UnitId, fn, Address, QuantityOrValue, Transport, txId),
            15 => ModbusFrame.BuildWriteMultiple(UnitId, fn, Address, ModbusFrame.ParseMultiValues(BatchValuesText, isCoils: true), Transport, txId),
            16 => ModbusFrame.BuildWriteMultiple(UnitId, fn, Address, ModbusFrame.ParseMultiValues(BatchValuesText, isCoils: false), Transport, txId),
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
            RequestPreviewHex = $"参数错误: {ex.Message}";
        }
    }

    private async Task ExecuteModbusAsync()
    {
        DecodedItems.Clear();
        ResponseHex = string.Empty;
        try
        {
            ushort txId = Transport == ModbusTransport.Tcp ? _transactionId++ : (ushort)1;
            if (_transactionId == 0) _transactionId = 1;

            var frame = BuildFrameForTxId(txId);
            RequestPreviewHex = ByteUtils.BytesToHex(frame);

            var respBytes = await _comm.ExchangeModbusAsync(frame, Transport, TimeoutMs, retries: 1);
            ResponseHex = ByteUtils.BytesToHex(respBytes);

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
            else
            {
                SummaryText = $"写入成功 (功能码 0x{parsed.FunctionCode:X2}，起始地址 {Address})";
            }
        }
        catch (Exception ex)
        {
            SummaryText = $"执行失败: {ex.Message}";
        }
    }
}
