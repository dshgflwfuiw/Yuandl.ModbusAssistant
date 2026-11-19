using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.Models;
using Yuandl.ModbusAssistant.Protocol.Modbus;
using Yuandl.ModbusAssistant.Services;

namespace Yuandl.ModbusAssistant.ViewModels.Pages;

public enum MonitorDataSource
{
    ModbusPolling,
    ServerLiveListener
}

public enum MonitorViewLayout
{
    Split,
    ScopeOnly,
    TableOnly
}

public enum TriggerEdgeMode
{
    Rising,
    Falling
}

public sealed class RegisterMonitorViewModel : ObservableObject
{
    private static readonly string[] ChannelPalette =
    {
        "#22D3EE", "#34D399", "#F59E0B", "#FB7185",
        "#A78BFA", "#60A5FA", "#F472B6", "#FACC15"
    };

    private readonly CommunicationService _comm;
    private readonly ModbusServerService _server;
    private readonly DispatcherTimer _pollTimer;
    private bool _isPollingTick;
    private int _sampleCapacity = 600;

    public RegisterMonitorViewModel(CommunicationService comm, ModbusServerService server)
    {
        _comm = comm;
        _server = server;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += async (_, _) => await OnTimerTickAsync();

        _server.RegistersMutated += (_, _) => RunOnUi(() =>
        {
            if (DataSource == MonitorDataSource.ServerLiveListener)
                SampleServerRegisters();
        });

        StartCommand = new RelayCommand(StartMonitoring);
        StopCommand = new RelayCommand(StopMonitoring);
        ToggleFreezeCommand = new RelayCommand(() =>
        {
            IsFrozen = !IsFrozen;
            ScopeUpdated?.Invoke(this, EventArgs.Empty);
        });
        ClearScopeCommand = new RelayCommand(() =>
        {
            Samples.Clear();
            ScopeUpdated?.Invoke(this, EventArgs.Empty);
        });
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ToggleChannelScopeCommand = new RelayCommand(p =>
        {
            if (p is RegisterMonitorRow row)
            {
                row.IsEnabledInScope = !row.IsEnabledInScope;
                var ch = ScopeChannels.FirstOrDefault(c => c.Id == row.ChannelId);
                if (ch != null) ch.Enabled = row.IsEnabledInScope;
                ScopeUpdated?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public ObservableCollection<RegisterMonitorRow> Rows { get; } = new();
    public ObservableCollection<ScopeChannelConfig> ScopeChannels { get; } = new();
    public List<ScopeSample> Samples { get; } = new();

    public event EventHandler? ScopeUpdated;

    public IReadOnlyList<MonitorDataSource> DataSources { get; } = Enum.GetValues<MonitorDataSource>();
    public IReadOnlyList<MonitorViewLayout> ViewLayouts { get; } = Enum.GetValues<MonitorViewLayout>();
    public IReadOnlyList<ModbusTransport> Transports { get; } = Enum.GetValues<ModbusTransport>();
    public IReadOnlyList<RegisterDataType> DataTypes { get; } = Enum.GetValues<RegisterDataType>();
    public IReadOnlyList<RegisterByteOrder> ByteOrders { get; } = Enum.GetValues<RegisterByteOrder>();
    public IReadOnlyList<int> TimeWindows { get; } = new[] { 10, 30, 60, 120 };
    public IReadOnlyList<TriggerEdgeMode> TriggerEdges { get; } = Enum.GetValues<TriggerEdgeMode>();
    public IReadOnlyList<byte> ReadFunctions { get; } = new byte[] { 1, 2, 3, 4 };

    private MonitorDataSource _dataSource = MonitorDataSource.ModbusPolling;
    public MonitorDataSource DataSource
    {
        get => _dataSource;
        set
        {
            if (SetProperty(ref _dataSource, value))
            {
                OnPropertyChanged(nameof(IsPollingMode));
                OnPropertyChanged(nameof(IsServerLiveMode));
            }
        }
    }

    public bool IsPollingMode => DataSource == MonitorDataSource.ModbusPolling;
    public bool IsServerLiveMode => DataSource == MonitorDataSource.ServerLiveListener;

    private MonitorViewLayout _viewLayout = MonitorViewLayout.Split;
    public MonitorViewLayout ViewLayout
    {
        get => _viewLayout;
        set
        {
            if (SetProperty(ref _viewLayout, value))
            {
                OnPropertyChanged(nameof(ShowScope));
                OnPropertyChanged(nameof(ShowTable));
            }
        }
    }

    public bool ShowScope => ViewLayout is MonitorViewLayout.Split or MonitorViewLayout.ScopeOnly;
    public bool ShowTable => ViewLayout is MonitorViewLayout.Split or MonitorViewLayout.TableOnly;

    private ModbusTransport _transport = ModbusTransport.Tcp;
    public ModbusTransport Transport { get => _transport; set => SetProperty(ref _transport, value); }

    private byte _unitId = 1;
    public byte UnitId { get => _unitId; set => SetProperty(ref _unitId, value); }

    private byte _functionCode = 3;
    public byte FunctionCode
    {
        get => _functionCode;
        set
        {
            if (SetProperty(ref _functionCode, value))
            {
                if (_functionCode is 1 or 2)
                    DataType = RegisterDataType.Bool;
                else if (DataType == RegisterDataType.Bool)
                    DataType = RegisterDataType.UInt16;
                OnPropertyChanged(nameof(IsBitFunction));
            }
        }
    }

    public bool IsBitFunction => FunctionCode is 1 or 2;

    private ushort _startAddress;
    public ushort StartAddress { get => _startAddress; set => SetProperty(ref _startAddress, value); }

    private ushort _quantity = 4;
    public ushort Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }

    private RegisterDataType _dataType = RegisterDataType.UInt16;
    public RegisterDataType DataType { get => _dataType; set => SetProperty(ref _dataType, value); }

    private RegisterByteOrder _byteOrder = RegisterByteOrder.BigEndian;
    public RegisterByteOrder ByteOrder { get => _byteOrder; set => SetProperty(ref _byteOrder, value); }

    private double _scale = 1.0;
    public double Scale { get => _scale; set => SetProperty(ref _scale, value); }

    private int _intervalMs = 500;
    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            if (SetProperty(ref _intervalMs, Math.Clamp(value, 100, 10000)))
                _pollTimer.Interval = TimeSpan.FromMilliseconds(_intervalMs);
        }
    }

    private bool _isMonitoring;
    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set => SetProperty(ref _isMonitoring, value);
    }

    private bool _isFrozen;
    public bool IsFrozen
    {
        get => _isFrozen;
        set => SetProperty(ref _isFrozen, value);
    }

    private int _timeWindowSec = 30;
    public int TimeWindowSec
    {
        get => _timeWindowSec;
        set
        {
            if (SetProperty(ref _timeWindowSec, value))
                ScopeUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _autoScaleY = true;
    public bool AutoScaleY
    {
        get => _autoScaleY;
        set
        {
            if (SetProperty(ref _autoScaleY, value))
                ScopeUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private double _manualYMin;
    public double ManualYMin { get => _manualYMin; set { if (SetProperty(ref _manualYMin, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private double _manualYMax = 100;
    public double ManualYMax { get => _manualYMax; set { if (SetProperty(ref _manualYMax, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private bool _triggerEnabled;
    public bool TriggerEnabled { get => _triggerEnabled; set { if (SetProperty(ref _triggerEnabled, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private ScopeChannelConfig? _triggerChannel;
    public ScopeChannelConfig? TriggerChannel { get => _triggerChannel; set { if (SetProperty(ref _triggerChannel, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private TriggerEdgeMode _triggerEdge = TriggerEdgeMode.Rising;
    public TriggerEdgeMode TriggerEdge { get => _triggerEdge; set { if (SetProperty(ref _triggerEdge, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private double _triggerLevel = 0.5;
    public double TriggerLevel { get => _triggerLevel; set { if (SetProperty(ref _triggerLevel, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private string _statusMessage = "监听未启动";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleFreezeCommand { get; }
    public RelayCommand ClearScopeCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ToggleChannelScopeCommand { get; }

    public void StartMonitoring()
    {
        if (DataSource == MonitorDataSource.ModbusPolling && _comm.State != ChannelState.Connected)
        {
            StatusMessage = "错误：Modbus 轮询需要先建立连接";
            return;
        }

        _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(IntervalMs, 100, 10000));
        _pollTimer.Start();
        IsMonitoring = true;
        StatusMessage = DataSource == MonitorDataSource.ServerLiveListener
            ? "正在实时监听本机 Modbus 服务器内存..."
            : $"正在轮询 FC{FunctionCode:D2} 地址 {StartAddress}...";

        if (DataSource == MonitorDataSource.ServerLiveListener)
            SampleServerRegisters();
        else
            _ = OnTimerTickAsync();
    }

    public void StopMonitoring()
    {
        _pollTimer.Stop();
        IsMonitoring = false;
        StatusMessage = "监听已停止";
    }

    private async Task OnTimerTickAsync()
    {
        if (!IsMonitoring || _isPollingTick) return;
        _isPollingTick = true;
        try
        {
            if (DataSource == MonitorDataSource.ServerLiveListener)
            {
                SampleServerRegisters();
                return;
            }

            if (_comm.State != ChannelState.Connected)
            {
                StopMonitoring();
                StatusMessage = "连接已断开，轮询停止";
                return;
            }

            var req = ModbusFrame.BuildRequest(UnitId, FunctionCode, StartAddress, Quantity, Transport);
            var resp = await _comm.ExchangeModbusAsync(req, Transport, 1000, 0);
            var parsed = ModbusFrame.ParseResponse(resp, Transport);

            if (parsed.IsException)
            {
                StatusMessage = $"轮询异常: {ModbusException.Describe(parsed.ExceptionCode ?? 0)}";
                return;
            }

            var newRows = new List<RegisterMonitorRow>();
            if (FunctionCode is 1 or 2)
            {
                var bits = ModbusFrame.ExtractBits(parsed.Payload, Quantity);
                for (int i = 0; i < bits.Length; i++)
                {
                    int addr = StartAddress + i;
                    double val = bits[i] ? 1.0 : 0.0;
                    newRows.Add(new RegisterMonitorRow
                    {
                        ChannelId = $"B_{FunctionCode}_{addr}",
                        Label = $"Bit {addr}",
                        Address = addr,
                        RawValue = bits[i] ? "true" : "false",
                        Value = val,
                        IsDigital = true,
                        TraceColorHex = ChannelPalette[newRows.Count % ChannelPalette.Length]
                    });
                }
            }
            else
            {
                int width = RegisterValueConverter.GetWidth(DataType);
                int idx = 0;
                for (int i = 0; i + width <= parsed.Registers.Length; i += width)
                {
                    var slice = parsed.Registers.Skip(i).Take(width).ToArray();
                    double val = RegisterValueConverter.Convert(slice, DataType, ByteOrder, Scale);
                    int addr = StartAddress + i;
                    bool isDigital = DataType == RegisterDataType.Bool;
                    newRows.Add(new RegisterMonitorRow
                    {
                        ChannelId = $"R_{FunctionCode}_{addr}",
                        Label = $"Reg {addr}",
                        Address = addr,
                        RawValue = string.Join(" ", slice.Select(r => $"0x{r:X4}")),
                        Value = val,
                        IsDigital = isDigital,
                        TraceColorHex = ChannelPalette[idx % ChannelPalette.Length]
                    });
                    idx++;
                }
            }

            ApplySampleBatch(newRows);
            StatusMessage = $"采样成功 ({newRows.Count} 通道) @ {DateTime.Now:HH:mm:ss.fff}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"轮询失败: {ex.Message}";
        }
        finally
        {
            _isPollingTick = false;
        }
    }

    private void SampleServerRegisters()
    {
        var defs = _server.GetDefinitionsWithCurrentValues();
        var newRows = new List<RegisterMonitorRow>();
        for (int i = 0; i < defs.Count; i++)
        {
            var d = defs[i];
            bool isDigital = d.Area is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput || d.DataType == ServerRegisterDataType.Bit;
            double numeric = 0;
            if (isDigital)
                numeric = d.CurrentValue.Equals("true", StringComparison.OrdinalIgnoreCase) || d.CurrentValue == "1" ? 1.0 : 0.0;
            else
                double.TryParse(d.CurrentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numeric);

            newRows.Add(new RegisterMonitorRow
            {
                ChannelId = $"{d.Area}_{d.Address}",
                Label = $"{ShortArea(d.Area)} {d.Address}",
                Address = d.Address,
                RawValue = d.CurrentValue,
                Value = numeric,
                IsDigital = isDigital,
                Note = d.Note,
                TraceColorHex = ChannelPalette[i % ChannelPalette.Length]
            });
        }

        ApplySampleBatch(newRows);
        StatusMessage = $"服务器实时监听：{newRows.Count} 通道 @ {DateTime.Now:HH:mm:ss.fff}";
    }

    private static string ShortArea(ServerRegisterArea area) => area switch
    {
        ServerRegisterArea.Coil => "Coil",
        ServerRegisterArea.DiscreteInput => "DI",
        ServerRegisterArea.InputRegister => "IR",
        _ => "HR"
    };

    private void ApplySampleBatch(List<RegisterMonitorRow> incoming)
    {
        var prevEnabled = Rows.ToDictionary(r => r.ChannelId, r => r.IsEnabledInScope);
        Rows.Clear();
        foreach (var row in incoming)
        {
            if (prevEnabled.TryGetValue(row.ChannelId, out bool enabled))
                row.IsEnabledInScope = enabled;
            Rows.Add(row);
        }

        // Sync ScopeChannels
        var existingTriggerId = TriggerChannel?.Id;
        ScopeChannels.Clear();
        foreach (var row in Rows)
        {
            ScopeChannels.Add(new ScopeChannelConfig
            {
                Id = row.ChannelId,
                Label = row.Label,
                ColorHex = row.TraceColorHex,
                Enabled = row.IsEnabledInScope,
                IsDigital = row.IsDigital,
                LatestText = row.DisplayValue
            });
        }

        if (TriggerChannel == null || ScopeChannels.All(c => c.Id != existingTriggerId))
            TriggerChannel = ScopeChannels.FirstOrDefault();
        else
            TriggerChannel = ScopeChannels.FirstOrDefault(c => c.Id == existingTriggerId);

        if (!IsFrozen && incoming.Count > 0)
        {
            var sample = new ScopeSample
            {
                Timestamp = DateTime.Now,
                Values = incoming.ToDictionary(r => r.ChannelId, r => r.Value)
            };
            Samples.Add(sample);
            while (Samples.Count > _sampleCapacity)
                Samples.RemoveAt(0);
        }

        ScopeUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void ExportCsv()
    {
        if (Rows.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"register-monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ChannelId,Label,Address,RawValue,NumericValue,IsDigital,Note");
            foreach (var r in Rows)
                sb.AppendLine($"\"{r.ChannelId}\",\"{r.Label}\",{r.Address},\"{r.RawValue}\",{r.Value},{r.IsDigital},\"{r.Note}\"");
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"已导出 CSV: {dlg.FileName}";
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
