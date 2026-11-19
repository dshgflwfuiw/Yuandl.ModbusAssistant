using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using CommunityToolkit.Mvvm.Input;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

/// <summary>
/// 寄存器监视页面 ViewModel
/// 负责连续寄存器的自动定时轮询读取、多类型转换及大小端解析
/// </summary>
public partial class RegisterMonitorViewModel : ViewModelBase
{
    public partial class RegisterRow : ObservableObject
    {
        public ushort Address { get; init; }
        public ushort RawValue { get; init; }
        [ObservableProperty] private object value = 0d;
        [ObservableProperty] private string note = string.Empty;
    }
    private readonly HslModbusService? _modbus;
    private CancellationTokenSource? _pollCts;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    [ObservableProperty] private ObservableCollection<RegisterRow> rows = [];
    [ObservableProperty] private RegisterDataType selectedDataType = RegisterDataType.UInt16;
    [ObservableProperty] private RegisterByteOrder selectedByteOrder = RegisterByteOrder.BigEndian;
    [ObservableProperty] private double scale = 1;
    [ObservableProperty] private byte slaveId = 1;
    [ObservableProperty] private bool isPolling;
    [ObservableProperty] private string statusText = "未开始读取";
    public RegisterDataType[] DataTypeOptions { get; } = Enum.GetValues<RegisterDataType>();
    public RegisterByteOrder[] ByteOrderOptions { get; } = Enum.GetValues<RegisterByteOrder>();
    [ObservableProperty]
    private ushort startAddress = 0;

    [ObservableProperty]
    private ushort registerCount = 10;

    [ObservableProperty]
    private int pollIntervalMs = 1000;

    public RegisterMonitorViewModel(HslModbusService? modbus = null)
    {
        _modbus = modbus;
        Title = "寄存器监视";
    }

    public void UpdateValues(ReadOnlySpan<ushort> values)
    {
        Rows.Clear();
        var width = SelectedDataType is RegisterDataType.Int32 or RegisterDataType.UInt32 or RegisterDataType.Float ? 2 : SelectedDataType == RegisterDataType.Double ? 4 : 1;
        for (var i = 0; i + width <= values.Length; i += width) Rows.Add(new RegisterRow { Address = (ushort)(StartAddress + i), RawValue = values[i], Value = RegisterValueConverter.Convert(values.Slice(i, width), SelectedDataType, SelectedByteOrder, Scale) });
    }

    [RelayCommand]
    public async Task ReadOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_modbus is null) { StatusText = "未配置 Hsl Modbus 服务"; return; }
        if (!await _pollLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            IsBusy = true;
            var values = await _modbus.ReadHoldingRegistersAsync(StartAddress, RegisterCount, SlaveId, cancellationToken);
            UpdateValues(values);
            StatusText = $"读取成功，{values.Length} 个寄存器";
        }
        catch (OperationCanceledException)
        {
            StatusText = "读取已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _pollLock.Release();
        }
    }

    [RelayCommand]
    public void TogglePolling()
    {
        if (IsPolling) { _pollCts?.Cancel(); IsPolling = false; return; }
        PollIntervalMs = Math.Max(100, PollIntervalMs); _pollCts = new CancellationTokenSource(); IsPolling = true; _ = PollLoopAsync(_pollCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReadOnceAsync(token).ConfigureAwait(false);
                try
                {
                    await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            IsPolling = false;
        }
    }

    public void StopPolling() { _pollCts?.Cancel(); IsPolling = false; }
}
