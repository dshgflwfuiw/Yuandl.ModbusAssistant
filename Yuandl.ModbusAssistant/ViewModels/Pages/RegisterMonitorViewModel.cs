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
        "#22D3EE", "#F59E0B", "#34D399", "#FB7185",
        "#60A5FA", "#A78BFA", "#F472B6", "#A3E635"
    };

    private readonly CommunicationService _comm;
    private readonly ModbusServerService _server;
    private readonly DispatcherTimer _pollTimer;
    private readonly Dictionary<string, string> _notesByKey = new();
    private readonly Dictionary<string, bool> _enabledOverrides = new();
    private bool _isPollingTick;
    private const int SampleCapacity = 600;

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

        SampleOnceCommand = new RelayCommand(SampleOnce);
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
            RefreshChannelTelemetry();
            ScopeUpdated?.Invoke(this, EventArgs.Empty);
        });
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ToggleChannelScopeCommand = new RelayCommand(p =>
        {
            string? id = p switch
            {
                RegisterMonitorRow row => row.ChannelId,
                ScopeChannelConfig cfg => cfg.Id,
                string s => s,
                _ => null
            };
            if (id != null) ToggleChannelById(id);
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
    public IReadOnlyList<int> TimeWindows { get; } = new[] { 15, 30, 60, 120, 300 };
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
                StopMonitoring();
                Samples.Clear();
                IsFrozen = false;
                OnPropertyChanged(nameof(IsPollingMode));
                OnPropertyChanged(nameof(IsServerLiveMode));
                OnPropertyChanged(nameof(SampleOnceLabel));
                if (_dataSource == MonitorDataSource.ServerLiveListener)
                    SampleServerRegisters();
                ScopeUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsPollingMode => DataSource == MonitorDataSource.ModbusPolling;
    public bool IsServerLiveMode => DataSource == MonitorDataSource.ServerLiveListener;
    public string SampleOnceLabel => IsServerLiveMode ? "采集一帧" : "读取一次";

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
                OnPropertyChanged(nameof(IsRegisterFunction));
            }
        }
    }

    public bool IsBitFunction => FunctionCode is 1 or 2;
    public bool IsRegisterFunction => !IsBitFunction;

    private ushort _startAddress;
    public ushort StartAddress { get => _startAddress; set => SetProperty(ref _startAddress, value); }

    private ushort _quantity = 6;
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
    public bool IsMonitoring { get => _isMonitoring; private set => SetProperty(ref _isMonitoring, value); }

    private bool _isFrozen;
    public bool IsFrozen { get => _isFrozen; set => SetProperty(ref _isFrozen, value); }

    private int _timeWindowSec = 60;
    public int TimeWindowSec
    {
        get => _timeWindowSec;
        set
        {
            if (SetProperty(ref _timeWindowSec, value))
            {
                RefreshChannelTelemetry();
                ScopeUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool _autoScaleY = true;
    public bool AutoScaleY
    {
        get => _autoScaleY;
        set
        {
            if (SetProperty(ref _autoScaleY, value))
            {
                OnPropertyChanged(nameof(IsManualY));
                ScopeUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsManualY => !AutoScaleY;

    private double _manualYMin;
    public double ManualYMin { get => _manualYMin; set { if (SetProperty(ref _manualYMin, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private double _manualYMax = 100;
    public double ManualYMax { get => _manualYMax; set { if (SetProperty(ref _manualYMax, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

    private bool _showMarkers;
    public bool ShowMarkers { get => _showMarkers; set { if (SetProperty(ref _showMarkers, value)) ScopeUpdated?.Invoke(this, EventArgs.Empty); } }

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

    public RelayCommand SampleOnceCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleFreezeCommand { get; }
    public RelayCommand ClearScopeCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ToggleChannelScopeCommand { get; }

    public void SampleOnce()
    {
        if (DataSource == MonitorDataSource.ServerLiveListener)
            SampleServerRegisters();
        else
            _ = OnTimerTickAsync(forceSingle: true);
    }

    public void StartMonitoring()
    {
        if (DataSource == MonitorDataSource.ModbusPolling && _comm.State != ChannelState.Connected)
        {
            StatusMessage = "错误：Modbus 轮询需要先在「通讯连接」建立连接";
            return;
        }

        _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(IntervalMs, 100, 10000));
        _pollTimer.Start();
        IsMonitoring = true;
        StatusMessage = DataSource == MonitorDataSource.ServerLiveListener
            ? "正在实时监听内置 Modbus 服务器内存..."
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

    private void ToggleChannelById(string channelId)
    {
        var row = Rows.FirstOrDefault(r => r.ChannelId == channelId);
        bool next = row != null ? !row.IsEnabledInScope : !(_enabledOverrides.TryGetValue(channelId, out bool prev) && prev);
        _enabledOverrides[channelId] = next;
        if (row != null) row.IsEnabledInScope = next;

        var ch = ScopeChannels.FirstOrDefault(c => c.Id == channelId);
        if (ch != null) ch.Enabled = next;

        // Refresh collection item binding cleanly
        int rowIdx = row != null ? Rows.IndexOf(row) : -1;
        if (rowIdx >= 0) Rows[rowIdx] = row!;
        int chIdx = ch != null ? ScopeChannels.IndexOf(ch) : -1;
        if (chIdx >= 0) ScopeChannels[chIdx] = ch!;

        ScopeUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateRowNote(string channelId, string note)
    {
        _notesByKey[channelId] = note;
        var ch = ScopeChannels.FirstOrDefault(c => c.Id == channelId);
        var row = Rows.FirstOrDefault(r => r.ChannelId == channelId);
        if (ch != null && row != null)
        {
            ch.Label = string.IsNullOrWhiteSpace(note) ? row.Label : $"{row.Label} ({note})";
            int idx = ScopeChannels.IndexOf(ch);
            if (idx >= 0) ScopeChannels[idx] = ch;
        }
    }

    private async Task OnTimerTickAsync(bool forceSingle = false)
    {
        if ((!IsMonitoring && !forceSingle) || _isPollingTick) return;
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
                if (IsMonitoring) StopMonitoring();
                StatusMessage = "请先在「通讯连接」建立连接后再读取或轮询";
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
                string prefix = FunctionCode == 1 ? "Coil" : "DI";
                for (int i = 0; i < bits.Length; i++)
                {
                    int addr = StartAddress + i;
                    string id = $"B_{FunctionCode}_{addr}";
                    double val = bits[i] ? 1.0 : 0.0;
                    newRows.Add(new RegisterMonitorRow
                    {
                        ChannelId = id,
                        Label = $"{prefix} #{addr}",
                        Address = addr,
                        RawValue = bits[i] ? "1 (true)" : "0 (false)",
                        Value = val,
                        IsDigital = true,
                        Note = _notesByKey.GetValueOrDefault(id, string.Empty),
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
                    bool isBoolWord = DataType == RegisterDataType.Bool;
                    double val = RegisterValueConverter.Convert(slice, DataType, ByteOrder, isBoolWord ? 1.0 : Scale);
                    int addr = StartAddress + i;
                    string id = $"R_{FunctionCode}_{addr}";
                    newRows.Add(new RegisterMonitorRow
                    {
                        ChannelId = id,
                        Label = $"Addr {addr}",
                        Address = addr,
                        RawValue = string.Join(" ", slice.Select(r => $"0x{r:X4}")),
                        Value = val,
                        IsDigital = isBoolWord,
                        Note = _notesByKey.GetValueOrDefault(id, string.Empty),
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
            StatusMessage = $"采集失败: {ex.Message}";
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
            string id = $"{d.Area}_{d.Address}";
            bool isDigital = d.Area is ServerRegisterArea.Coil or ServerRegisterArea.DiscreteInput || d.DataType == ServerRegisterDataType.Bit;
            double numeric = 0;
            if (isDigital)
                numeric = d.CurrentValue.Equals("true", StringComparison.OrdinalIgnoreCase) || d.CurrentValue == "1" ? 1.0 : 0.0;
            else
                double.TryParse(d.CurrentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numeric);

            string note = _notesByKey.TryGetValue(id, out var customNote) ? customNote : d.Note;

            newRows.Add(new RegisterMonitorRow
            {
                ChannelId = id,
                Label = $"{ShortArea(d.Area)} #{d.Address}",
                Address = d.Address,
                RawValue = isDigital ? (numeric >= 0.5 ? "true (1)" : "false (0)") : d.CurrentValue,
                Value = numeric,
                IsDigital = isDigital,
                Note = note,
                TraceColorHex = ChannelPalette[i % ChannelPalette.Length]
            });
        }

        ApplySampleBatch(newRows);
        StatusMessage = newRows.Count > 0
            ? $"服务器实时监听：{newRows.Count} 通道 @ {DateTime.Now:HH:mm:ss.fff}"
            : "内置服务器暂无已定义的寄存器";
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
        Rows.Clear();
        for (int i = 0; i < incoming.Count; i++)
        {
            var row = incoming[i];
            row.OnNoteChanged = UpdateRowNote;
            row.IsEnabledInScope = _enabledOverrides.TryGetValue(row.ChannelId, out bool enabled)
                ? enabled
                : i < 6;
            Rows.Add(row);
        }

        if (!IsFrozen && incoming.Count > 0)
        {
            var sample = new ScopeSample
            {
                Timestamp = DateTime.Now,
                Values = incoming.ToDictionary(r => r.ChannelId, r => r.Value)
            };
            Samples.Add(sample);
            while (Samples.Count > SampleCapacity)
                Samples.RemoveAt(0);
        }

        RefreshChannelTelemetry();
        ScopeUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshChannelTelemetry()
    {
        string? existingTriggerId = TriggerChannel?.Id;
        DateTime endTime = Samples.Count > 0 ? Samples[^1].Timestamp : DateTime.Now;
        DateTime startTime = endTime.AddSeconds(-TimeWindowSec);
        var windowSamples = Samples.Where(s => s.Timestamp >= startTime).ToList();

        ScopeChannels.Clear();
        foreach (var row in Rows)
        {
            double min = double.MaxValue;
            double max = double.MinValue;
            int cnt = 0;
            foreach (var s in windowSamples)
            {
                if (s.Values.TryGetValue(row.ChannelId, out double v) && double.IsFinite(v))
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                    cnt++;
                }
            }

            ScopeChannels.Add(new ScopeChannelConfig
            {
                Id = row.ChannelId,
                Label = string.IsNullOrWhiteSpace(row.Note) ? row.Label : $"{row.Label} ({row.Note})",
                ColorHex = row.TraceColorHex,
                Enabled = row.IsEnabledInScope,
                IsDigital = row.IsDigital,
                LatestText = row.DisplayValue,
                RangeText = !row.IsDigital && cnt > 0 ? $"[{min:0.##}..{max:0.##}]" : string.Empty
            });
        }

        TriggerChannel = ScopeChannels.FirstOrDefault(c => c.Id == existingTriggerId)
            ?? ScopeChannels.FirstOrDefault(c => c.Enabled)
            ?? ScopeChannels.FirstOrDefault();
    }

    private void ExportCsv()
    {
        if (Samples.Count == 0 && Rows.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"modbus-oscilloscope-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            if (Samples.Count > 0 && ScopeChannels.Count > 0)
            {
                sb.AppendLine("Timestamp," + string.Join(",", ScopeChannels.Select(c => $"\"{c.Label}\"")));
                foreach (var s in Samples)
                {
                    var vals = ScopeChannels.Select(c => s.Values.TryGetValue(c.Id, out double v) ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
                    sb.AppendLine($"{s.Timestamp:O}," + string.Join(",", vals));
                }
            }
            else
            {
                sb.AppendLine("ChannelId,Label,Address,RawValue,NumericValue,IsDigital,Note");
                foreach (var r in Rows)
                    sb.AppendLine($"\"{r.ChannelId}\",\"{r.Label}\",{r.Address},\"{r.RawValue}\",{r.Value},{r.IsDigital},\"{r.Note}\"");
            }
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
