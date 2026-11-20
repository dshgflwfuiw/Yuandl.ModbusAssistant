using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
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

public sealed class MonitorPollingData
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public byte FunctionCode { get; init; }
    public ushort StartAddress { get; init; }
    public ushort Quantity { get; init; }
    public RegisterDataType DataType { get; init; }
    public RegisterByteOrder ByteOrder { get; init; }
    public double Scale { get; init; }
    public string DisplayName => $"FC{FunctionCode:D2} · 地址 {StartAddress} · 数量 {Quantity}";
}

public sealed class MonitorPollingGroup : ObservableObject
{
    private string _name = string.Empty;
    private ModbusTransport _transport = ModbusTransport.Tcp;
    private byte _unitId = 1;
    private byte _functionCode = 3;
    private ushort _startAddress;
    private ushort _quantity = 6;
    private RegisterDataType _dataType = RegisterDataType.UInt16;
    private RegisterByteOrder _byteOrder = RegisterByteOrder.BigEndian;
    private double _scale = 1.0;
    private int _intervalMs = 500;
    private bool _isEnabled = true;
    private string _lastStatus = "等待采集";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public ModbusTransport Transport { get => _transport; set => SetProperty(ref _transport, value); }
    public byte UnitId { get => _unitId; set => SetProperty(ref _unitId, value); }
    public byte FunctionCode { get => _functionCode; set => SetProperty(ref _functionCode, value); }
    public ushort StartAddress { get => _startAddress; set => SetProperty(ref _startAddress, value); }
    public ushort Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }
    public RegisterDataType DataType { get => _dataType; set => SetProperty(ref _dataType, value); }
    public RegisterByteOrder ByteOrder { get => _byteOrder; set => SetProperty(ref _byteOrder, value); }
    public double Scale { get => _scale; set => SetProperty(ref _scale, value); }
    public int IntervalMs { get => _intervalMs; set => SetProperty(ref _intervalMs, Math.Clamp(value, 100, 10000)); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string LastStatus { get => _lastStatus; set => SetProperty(ref _lastStatus, value); }
    public ObservableCollection<MonitorPollingData> DataItems { get; } = new();
    public DateTime NextPollAt { get; set; }
}

public sealed class RegisterMonitorViewModel : ObservableObject, IDisposable
{
    private static readonly string[] ChannelPalette =
    {
        "#22D3EE", "#F59E0B", "#34D399", "#FB7185",
        "#60A5FA", "#A78BFA", "#F472B6", "#A3E635"
    };

    private readonly CommunicationService _comm;
    private readonly ModbusServerService _server;
    private readonly SettingsViewModel _settings;
    private readonly PersistenceService _persistence;
    private readonly object _pollSync = new();
    private CancellationTokenSource? _pollCts;
    private PeriodicTimer? _pollTimer;
    private Task? _pollLoopTask;
    private readonly Dictionary<string, string> _notesByKey = new();
    private readonly Dictionary<string, bool> _enabledOverrides = new();
    private readonly Dictionary<string, List<RegisterMonitorRow>> _latestRowsByGroup = new();
    private int _isPollingTick;
    private bool _isLoadingGroup;
    private bool _isInitializing;
    private const int SampleCapacity = 600;

    public RegisterMonitorViewModel(CommunicationService comm, ModbusServerService server, SettingsViewModel settings, PersistenceService persistence)
    {
        _comm = comm;
        _server = server;
        _settings = settings;
        _persistence = persistence;

        _server.RegistersMutated += (_, _) => RunOnUi(() =>
        {
            if (DataSource == MonitorDataSource.ServerLiveListener)
                SampleServerRegisters();
        });

        SampleOnceCommand = new AsyncRelayCommand(SampleOnceAsync, ex => StatusMessage = $"采集失败: {ex.Message}");
        StartCommand = new RelayCommand(StartMonitoring);
        StopCommand = new RelayCommand(() => StopMonitoring("用户停止监听"));
        AddPollingGroupCommand = new RelayCommand(AddPollingGroup);
        RemovePollingGroupCommand = new RelayCommand(RemoveSelectedPollingGroup, () => SelectedPollingGroup != null);
        AddPollingDataCommand = new RelayCommand(AddPollingData, () => SelectedPollingGroup != null);
        RemovePollingDataCommand = new RelayCommand(RemovePollingData);
        _isInitializing = true;
        bool seededDefault = false;
        try
        {
            seededDefault = LoadPollingConfiguration();
        }
        finally
        {
            _isInitializing = false;
        }
        if (seededDefault)
            SavePollingConfiguration();
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
    public ObservableCollection<MonitorPollingGroup> PollingGroups { get; } = new();
    public List<ScopeSample> Samples { get; } = new();

    private MonitorPollingGroup? _selectedPollingGroup;
    public MonitorPollingGroup? SelectedPollingGroup
    {
        get => _selectedPollingGroup;
        set
        {
            if (SetProperty(ref _selectedPollingGroup, value))
            {
                LoadSelectedPollingGroup();
                RemovePollingGroupCommand?.RaiseCanExecuteChanged();
                AddPollingDataCommand?.RaiseCanExecuteChanged();
                SavePollingConfiguration();
            }
        }
    }

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
                StopMonitoring("切换数据源");
                Samples.Clear();
                _latestRowsByGroup.Clear();
                Rows.Clear();
                ScopeChannels.Clear();
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
    public string SampleOnceLabel => IsServerLiveMode ? "采集一帧" : "采集所有启用组";

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
                OnPropertyChanged(nameof(ScopeRowHeight));
                OnPropertyChanged(nameof(ScopeTableSplitterHeight));
                OnPropertyChanged(nameof(TableRowHeight));
            }
        }
    }

    public bool ShowScope => ViewLayout is MonitorViewLayout.Split or MonitorViewLayout.ScopeOnly;
    public bool ShowTable => ViewLayout is MonitorViewLayout.Split or MonitorViewLayout.TableOnly;
    public GridLength ScopeRowHeight => ViewLayout == MonitorViewLayout.TableOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    public GridLength ScopeTableSplitterHeight => ViewLayout == MonitorViewLayout.Split ? new GridLength(6) : new GridLength(0);
    public GridLength TableRowHeight => ViewLayout == MonitorViewLayout.ScopeOnly
        ? new GridLength(0)
        : ViewLayout == MonitorViewLayout.TableOnly
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(120);

    private ModbusTransport _transport = ModbusTransport.Tcp;
    public ModbusTransport Transport { get => _transport; set { if (SetProperty(ref _transport, value)) UpdateSelectedGroup(g => g.Transport = value); } }

    private byte _unitId = 1;
    public byte UnitId { get => _unitId; set { if (SetProperty(ref _unitId, value)) UpdateSelectedGroup(g => g.UnitId = value); } }

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
                UpdateSelectedGroup(g =>
                {
                    g.FunctionCode = value;
                    g.DataType = DataType;
                });
            }
        }
    }

    public bool IsBitFunction => FunctionCode is 1 or 2;
    public bool IsRegisterFunction => !IsBitFunction;

    private ushort _startAddress;
    public ushort StartAddress { get => _startAddress; set { if (SetProperty(ref _startAddress, value)) UpdateSelectedGroup(g => g.StartAddress = value); } }

    private ushort _quantity = 6;
    public ushort Quantity { get => _quantity; set { if (SetProperty(ref _quantity, value)) UpdateSelectedGroup(g => g.Quantity = value); } }

    private RegisterDataType _dataType = RegisterDataType.UInt16;
    public RegisterDataType DataType { get => _dataType; set { if (SetProperty(ref _dataType, value)) UpdateSelectedGroup(g => g.DataType = value); } }

    private RegisterByteOrder _byteOrder = RegisterByteOrder.BigEndian;
    public RegisterByteOrder ByteOrder { get => _byteOrder; set { if (SetProperty(ref _byteOrder, value)) UpdateSelectedGroup(g => g.ByteOrder = value); } }

    private double _scale = 1.0;
    public double Scale { get => _scale; set { if (SetProperty(ref _scale, value)) UpdateSelectedGroup(g => g.Scale = value); } }

    private int _intervalMs = 500;
    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            if (SetProperty(ref _intervalMs, Math.Clamp(value, 100, 10000)))
            {
                UpdateSelectedGroup(g => g.IntervalMs = _intervalMs);
                lock (_pollSync)
                {
                    if (IsMonitoring && IsServerLiveMode && _pollTimer != null)
                        _pollTimer.Period = TimeSpan.FromMilliseconds(_intervalMs);
                }
            }
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

    public AsyncRelayCommand SampleOnceCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand AddPollingGroupCommand { get; }
    public RelayCommand RemovePollingGroupCommand { get; }
    public RelayCommand AddPollingDataCommand { get; }
    public RelayCommand RemovePollingDataCommand { get; }
    public RelayCommand ToggleFreezeCommand { get; }
    public RelayCommand ClearScopeCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ToggleChannelScopeCommand { get; }

    private MonitorPollingGroup CreatePollingGroup(string name) => new()
    {
        Name = name,
        Transport = Transport,
        UnitId = UnitId,
        FunctionCode = FunctionCode,
        StartAddress = StartAddress,
        Quantity = Quantity,
        DataType = DataType,
        ByteOrder = ByteOrder,
        Scale = Scale,
        IntervalMs = IntervalMs
    };

    private bool LoadPollingConfiguration()
    {
        var config = _persistence.LoadMonitorConfiguration();
        foreach (var note in config.ChannelNotes)
            _notesByKey[note.Key] = note.Value;

        foreach (var savedGroup in config.Groups)
        {
            var group = new MonitorPollingGroup
            {
                Id = savedGroup.Id,
                Name = savedGroup.Name,
                Transport = savedGroup.Transport,
                UnitId = savedGroup.UnitId,
                FunctionCode = savedGroup.FunctionCode,
                StartAddress = savedGroup.StartAddress,
                Quantity = savedGroup.Quantity,
                DataType = savedGroup.DataType,
                ByteOrder = savedGroup.ByteOrder,
                Scale = savedGroup.Scale,
                IntervalMs = savedGroup.IntervalMs,
                IsEnabled = savedGroup.IsEnabled
            };

            foreach (var savedItem in savedGroup.DataItems)
            {
                group.DataItems.Add(new MonitorPollingData
                {
                    Id = savedItem.Id,
                    FunctionCode = savedItem.FunctionCode,
                    StartAddress = savedItem.StartAddress,
                    Quantity = savedItem.Quantity,
                    DataType = savedItem.DataType,
                    ByteOrder = savedItem.ByteOrder,
                    Scale = savedItem.Scale
                });
            }

            AttachPollingGroup(group);
            PollingGroups.Add(group);
        }

        SelectedPollingGroup = PollingGroups.FirstOrDefault(g => g.Id == config.SelectedGroupId)
            ?? PollingGroups.FirstOrDefault();

        if (PollingGroups.Count == 0)
        {
            var initialGroup = CreatePollingGroup("采集组 1");
            AttachPollingGroup(initialGroup);
            PollingGroups.Add(initialGroup);
            SelectedPollingGroup = initialGroup;
            AddPollingData();
            return true;
        }
        return false;
    }

    private void AttachPollingGroup(MonitorPollingGroup group)
    {
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MonitorPollingGroup.Name)
                or nameof(MonitorPollingGroup.Transport)
                or nameof(MonitorPollingGroup.UnitId)
                or nameof(MonitorPollingGroup.FunctionCode)
                or nameof(MonitorPollingGroup.StartAddress)
                or nameof(MonitorPollingGroup.Quantity)
                or nameof(MonitorPollingGroup.DataType)
                or nameof(MonitorPollingGroup.ByteOrder)
                or nameof(MonitorPollingGroup.Scale)
                or nameof(MonitorPollingGroup.IntervalMs)
                or nameof(MonitorPollingGroup.IsEnabled))
                SavePollingConfiguration();
        };
        group.DataItems.CollectionChanged += (_, _) => SavePollingConfiguration();
    }

    private void SavePollingConfiguration()
    {
        if (_isInitializing) return;
        _persistence.SaveMonitorConfiguration(new MonitorConfiguration
        {
            SelectedGroupId = SelectedPollingGroup?.Id,
            ChannelNotes = new Dictionary<string, string>(_notesByKey),
            Groups = PollingGroups.Select(group => new MonitorPollingGroupConfiguration
            {
                Id = group.Id,
                Name = group.Name,
                Transport = group.Transport,
                UnitId = group.UnitId,
                FunctionCode = group.FunctionCode,
                StartAddress = group.StartAddress,
                Quantity = group.Quantity,
                DataType = group.DataType,
                ByteOrder = group.ByteOrder,
                Scale = group.Scale,
                IntervalMs = group.IntervalMs,
                IsEnabled = group.IsEnabled,
                DataItems = group.DataItems.Select(item => new MonitorPollingDataConfiguration
                {
                    Id = item.Id,
                    FunctionCode = item.FunctionCode,
                    StartAddress = item.StartAddress,
                    Quantity = item.Quantity,
                    DataType = item.DataType,
                    ByteOrder = item.ByteOrder,
                    Scale = item.Scale
                }).ToList()
            }).ToList()
        });
    }

    private void UpdateSelectedGroup(Action<MonitorPollingGroup> update)
    {
        if (!_isLoadingGroup && SelectedPollingGroup is { } group)
            update(group);
    }

    private void LoadSelectedPollingGroup()
    {
        if (SelectedPollingGroup is not { } group) return;

        _isLoadingGroup = true;
        Transport = group.Transport;
        UnitId = group.UnitId;
        FunctionCode = group.FunctionCode;
        StartAddress = group.StartAddress;
        Quantity = group.Quantity;
        DataType = group.DataType;
        ByteOrder = group.ByteOrder;
        Scale = group.Scale;
        IntervalMs = group.IntervalMs;
        _isLoadingGroup = false;
        OnPropertyChanged(nameof(IsBitFunction));
        OnPropertyChanged(nameof(IsRegisterFunction));
    }

    private void AddPollingGroup()
    {
        int index = 1;
        while (PollingGroups.Any(g => g.Name == $"采集组 {index}")) index++;
        var group = CreatePollingGroup($"采集组 {index}");
        group.DataItems.Clear();
        AttachPollingGroup(group);
        PollingGroups.Add(group);
        SelectedPollingGroup = group;
        SavePollingConfiguration();
        StatusMessage = $"已添加 {group.Name}，配置地址范围后点击“添加数据到当前组”";
    }

    private void AddPollingData()
    {
        if (SelectedPollingGroup is not { } group) return;

        var item = new MonitorPollingData
        {
            FunctionCode = FunctionCode,
            StartAddress = StartAddress,
            Quantity = Quantity,
            DataType = DataType,
            ByteOrder = ByteOrder,
            Scale = Scale
        };
        group.DataItems.Add(item);
        SavePollingConfiguration();
        StatusMessage = $"已将 {item.DisplayName} 添加到 {group.Name}";
    }

    private void RemovePollingData(object? parameter)
    {
        if (SelectedPollingGroup is not { } group || parameter is not MonitorPollingData item) return;
        group.DataItems.Remove(item);
        SavePollingConfiguration();
        string channelPrefix = $"{group.Id}_{item.Id}_";
        if (_latestRowsByGroup.TryGetValue(group.Id, out var groupRows))
        {
            groupRows.RemoveAll(row => row.ChannelId.StartsWith(channelPrefix, StringComparison.Ordinal));
            foreach (var sample in Samples)
            {
                foreach (string channelId in sample.Values.Keys.Where(id => id.StartsWith(channelPrefix, StringComparison.Ordinal)).ToList())
                    sample.Values.Remove(channelId);
            }
        }
        RebuildRowsFromLatestGroups();
        StatusMessage = $"已从 {group.Name} 移除 {item.DisplayName}";
    }

    private void RemoveSelectedPollingGroup()
    {
        if (SelectedPollingGroup is not { } group) return;
        PollingGroups.Remove(group);
        _latestRowsByGroup.Remove(group.Id);
        foreach (var sample in Samples)
        {
            foreach (var row in Rows.Where(r => r.ChannelId.StartsWith($"{group.Id}_", StringComparison.Ordinal)))
                sample.Values.Remove(row.ChannelId);
        }

        SelectedPollingGroup = PollingGroups.FirstOrDefault();
        SavePollingConfiguration();
        RebuildRowsFromLatestGroups();
        StatusMessage = $"已删除 {group.Name}";
    }

    private void RebuildRowsFromLatestGroups()
    {
        ApplySampleBatch(_latestRowsByGroup.Values.SelectMany(rows => rows).ToList());
    }

    public Task SampleOnceAsync()
    {
        if (DataSource == MonitorDataSource.ServerLiveListener)
        {
            SampleServerRegisters();
            return Task.CompletedTask;
        }
        return OnTimerTickAsync(forceSingle: true, CancellationToken.None);
    }

    public void StartMonitoring()
    {
        if (DataSource == MonitorDataSource.ModbusPolling && _comm.State != ChannelState.Connected)
        {
            StatusMessage = "错误：Modbus 轮询需要先在「通讯连接」建立连接";
            return;
        }

        if (DataSource == MonitorDataSource.ModbusPolling && !PollingGroups.Any(g => g.IsEnabled && g.DataItems.Count > 0))
        {
            StatusMessage = "请至少在一个已启用的采集组中添加数据";
            return;
        }

        StopMonitoring("重新启动监听");

        foreach (var group in PollingGroups.Where(g => g.IsEnabled && g.DataItems.Count > 0))
            group.NextPollAt = DateTime.MinValue;

        var interval = DataSource == MonitorDataSource.ServerLiveListener
            ? TimeSpan.FromMilliseconds(Math.Clamp(IntervalMs, 100, 10000))
            : TimeSpan.FromMilliseconds(100);

        CancellationTokenSource cts;
        PeriodicTimer timer;
        lock (_pollSync)
        {
            cts = new CancellationTokenSource();
            timer = new PeriodicTimer(interval);
            _pollCts = cts;
            _pollTimer = timer;
            IsMonitoring = true;
            _pollLoopTask = Task.Run(() => RunPollLoopAsync(timer, cts.Token), CancellationToken.None);
        }

        StatusMessage = DataSource == MonitorDataSource.ServerLiveListener
            ? "正在实时监听内置 Modbus 服务器内存..."
            : $"正在轮询 {PollingGroups.Count(g => g.IsEnabled)} 个采集组...";

        AppLogger.Info($"寄存器监视已启动: {StatusMessage}");

        if (DataSource == MonitorDataSource.ServerLiveListener)
            SampleServerRegisters();
        else
            _ = OnTimerTickAsync(forceSingle: false, cts.Token);
    }

    public void StopMonitoring() => StopMonitoring("停止监听");

    private void StopMonitoring(string reason)
    {
        CancellationTokenSource? cts;
        PeriodicTimer? timer;
        bool wasMonitoring = IsMonitoring;
        lock (_pollSync)
        {
            cts = _pollCts;
            _pollCts = null;
            timer = _pollTimer;
            _pollTimer = null;
            _pollLoopTask = null;
            IsMonitoring = false;
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        timer?.Dispose();

        StatusMessage = "监听已停止";
        if (wasMonitoring)
            AppLogger.Info($"寄存器监视停止: {reason}");
    }

    private async Task RunPollLoopAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (token.IsCancellationRequested || !IsMonitoring)
                    break;
                await OnTimerTickAsync(forceSingle: false, token);
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("寄存器监视后台轮询任务已取消");
        }
        catch (Exception ex)
        {
            AppLogger.Error("寄存器监视后台轮询任务异常退出", ex);
            RunOnUi(() =>
            {
                StopMonitoring($"异常退出: {ex.Message}");
                StatusMessage = $"采集失败: {ex.Message}";
            });
        }
    }

    private void ToggleChannelById(string channelId)
    {
        var row = Rows.FirstOrDefault(r => r.ChannelId == channelId);
        bool next = row != null ? !row.IsEnabledInScope : !(_enabledOverrides.TryGetValue(channelId, out bool prev) && prev);
        _enabledOverrides[channelId] = next;
        if (row != null) row.IsEnabledInScope = next;

        var ch = ScopeChannels.FirstOrDefault(c => c.Id == channelId);
        if (ch != null) ch.Enabled = next;

        ScopeUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateRowNote(string channelId, string note)
    {
        _notesByKey[channelId] = note;
        SavePollingConfiguration();
        var ch = ScopeChannels.FirstOrDefault(c => c.Id == channelId);
        var row = Rows.FirstOrDefault(r => r.ChannelId == channelId);
        if (ch != null && row != null)
            ch.Label = string.IsNullOrWhiteSpace(note) ? row.Label : $"{row.Label} ({note})";
    }

    private async Task OnTimerTickAsync(bool forceSingle, CancellationToken cancellationToken)
    {
        if (!IsMonitoring && !forceSingle)
            return;
        if (Interlocked.CompareExchange(ref _isPollingTick, 1, 0) != 0)
            return;

        try
        {
            if (DataSource == MonitorDataSource.ServerLiveListener)
            {
                RunOnUi(SampleServerRegisters);
                return;
            }

            if (_comm.State != ChannelState.Connected)
            {
                RunOnUi(() =>
                {
                    if (IsMonitoring) StopMonitoring("通道未连接");
                    StatusMessage = "请先在「通讯连接」建立连接后再读取或轮询";
                });
                return;
            }

            DateTime now = DateTime.Now;
            List<MonitorPollingGroup> dueGroups = new();
            RunOnUi(() =>
            {
                dueGroups = PollingGroups
                    .Where(g => g.IsEnabled && g.DataItems.Count > 0 && (forceSingle || g.NextPollAt <= now))
                    .ToList();
            });

            if (dueGroups.Count == 0)
            {
                if (forceSingle)
                    RunOnUi(() => StatusMessage = "请先在已启用的采集组中添加数据");
                return;
            }

            int successfulGroups = 0;
            foreach (var group in dueGroups)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var newRows = await ReadPollingGroupAsync(group, cancellationToken);
                    RunOnUi(() =>
                    {
                        _latestRowsByGroup[group.Id] = newRows;
                        group.LastStatus = $"正常 · {DateTime.Now:HH:mm:ss}";
                    });
                    successfulGroups++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"采集组 {group.Name} 读取失败: {ex.Message}");
                    RunOnUi(() => group.LastStatus = $"失败 · {ex.Message}");
                }
                finally
                {
                    if (IsMonitoring && !forceSingle)
                        group.NextPollAt = DateTime.Now.AddMilliseconds(group.IntervalMs);
                }
            }

            RunOnUi(() =>
            {
                var latestRows = _latestRowsByGroup.Values.SelectMany(rows => rows).ToList();
                ApplySampleBatch(latestRows);
                StatusMessage = $"采集完成：{successfulGroups}/{dueGroups.Count} 组成功，当前 {latestRows.Count} 个通道 @ {DateTime.Now:HH:mm:ss.fff}";
            });
        }
        catch (OperationCanceledException)
        {
            // normal shutdown/cancellation
        }
        catch (Exception ex)
        {
            AppLogger.Error("寄存器监视采集周期异常", ex);
            RunOnUi(() => StatusMessage = $"采集失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isPollingTick, 0);
        }
    }

    private async Task<List<RegisterMonitorRow>> ReadPollingGroupAsync(MonitorPollingGroup group, CancellationToken cancellationToken)
    {
        string groupId = group.Id;
        string groupName = group.Name;
        var transport = group.Transport;
        byte unitId = group.UnitId;
        int paletteOffset = 0;
        List<MonitorPollingData> itemsSnapshot = new();
        RunOnUi(() =>
        {
            paletteOffset = Math.Max(0, PollingGroups.IndexOf(group)) * 3;
            itemsSnapshot = group.DataItems.ToList();
        });

        var rows = new List<RegisterMonitorRow>();

        for (int itemIdx = 0; itemIdx < itemsSnapshot.Count; itemIdx++)
        {
            var item = itemsSnapshot[itemIdx];
            var req = ModbusFrame.BuildRequest(unitId, item.FunctionCode, item.StartAddress, item.Quantity, transport);
            var resp = await _comm.ExchangeModbusAsync(req, transport, _settings.DefaultTimeoutMs, _settings.DefaultRetryTimes, cancellationToken);
            bool stillExists = true;
            RunOnUi(() => stillExists = PollingGroups.Contains(group) && group.DataItems.Contains(item));
            if (!stillExists)
                continue;

            var parsed = ModbusFrame.ParseResponse(resp, transport);
            if (parsed.IsException)
                throw new InvalidOperationException($"{item.DisplayName}: {ModbusException.Describe(parsed.ExceptionCode ?? 0)}");

            int itemColorOffset = itemIdx * 2;
            if (item.FunctionCode is 1 or 2)
            {
                var bits = ModbusFrame.ExtractBits(parsed.Payload, item.Quantity);
                string prefix = item.FunctionCode == 1 ? "Coil" : "DI";
                for (int i = 0; i < bits.Length; i++)
                {
                    int address = item.StartAddress + i;
                    string id = $"{groupId}_{item.Id}_B_{item.FunctionCode}_{address}";
                    double value = bits[i] ? 1.0 : 0.0;
                    rows.Add(new RegisterMonitorRow
                    {
                        ChannelId = id,
                        Label = $"{groupName} · {prefix} #{address}",
                        Address = address,
                        RawValue = bits[i] ? "1 (true)" : "0 (false)",
                        Value = value,
                        IsDigital = true,
                        Note = _notesByKey.GetValueOrDefault(id, string.Empty),
                        TraceColorHex = ChannelPalette[(paletteOffset + itemColorOffset + rows.Count) % ChannelPalette.Length]
                    });
                }
            }
            else
            {
                int width = RegisterValueConverter.GetWidth(item.DataType);
                for (int i = 0; i + width <= parsed.Registers.Length; i += width)
                {
                    var slice = parsed.Registers.Skip(i).Take(width).ToArray();
                    bool isBoolWord = item.DataType == RegisterDataType.Bool;
                    double value = RegisterValueConverter.Convert(slice, item.DataType, item.ByteOrder, isBoolWord ? 1.0 : item.Scale);
                    int address = item.StartAddress + i;
                    string id = $"{groupId}_{item.Id}_R_{item.FunctionCode}_{address}";
                    rows.Add(new RegisterMonitorRow
                    {
                        ChannelId = id,
                        Label = $"{groupName} · Addr {address}",
                        Address = address,
                        RawValue = string.Join(" ", slice.Select(r => $"0x{r:X4}")),
                        Value = value,
                        IsDigital = isBoolWord,
                        Note = _notesByKey.GetValueOrDefault(id, string.Empty),
                        TraceColorHex = ChannelPalette[(paletteOffset + itemColorOffset + rows.Count) % ChannelPalette.Length]
                    });
                }
            }
        }

        return rows;
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
        var existingRows = Rows.ToDictionary(row => row.ChannelId, StringComparer.Ordinal);
        var updatedRows = new List<RegisterMonitorRow>(incoming.Count);
        for (int i = 0; i < incoming.Count; i++)
        {
            var sample = incoming[i];
            if (existingRows.TryGetValue(sample.ChannelId, out var row))
            {
                row.Label = sample.Label;
                row.Address = sample.Address;
                row.RawValue = sample.RawValue;
                row.Value = sample.Value;
                row.IsDigital = sample.IsDigital;
                row.TraceColorHex = sample.TraceColorHex;
                row.OnNoteChanged = UpdateRowNote;
            }
            else
            {
                row = sample;
                row.OnNoteChanged = UpdateRowNote;
                row.IsEnabledInScope = _enabledOverrides.TryGetValue(row.ChannelId, out bool enabled)
                    ? enabled
                    : Rows.Count + updatedRows.Count < 6;
            }

            updatedRows.Add(row);
        }

        SyncObservableCollection(Rows, updatedRows, row => row.ChannelId);

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
        var existingChannels = ScopeChannels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);
        var updatedChannels = new List<ScopeChannelConfig>(Rows.Count);
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

            if (!existingChannels.TryGetValue(row.ChannelId, out var channel))
                channel = new ScopeChannelConfig { Id = row.ChannelId };

            channel.Label = string.IsNullOrWhiteSpace(row.Note) ? row.Label : $"{row.Label} ({row.Note})";
            channel.ColorHex = row.TraceColorHex;
            channel.Enabled = row.IsEnabledInScope;
            channel.IsDigital = row.IsDigital;
            channel.LatestText = row.DisplayValue;
            channel.RangeText = !row.IsDigital && cnt > 0 ? $"[{min:0.##}..{max:0.##}]" : string.Empty;
            updatedChannels.Add(channel);
        }

        SyncObservableCollection(ScopeChannels, updatedChannels, channel => channel.Id);

        var nextTrigger = updatedChannels.FirstOrDefault(c => c.Id == existingTriggerId)
            ?? updatedChannels.FirstOrDefault(c => c.Enabled)
            ?? updatedChannels.FirstOrDefault();
        if (!ReferenceEquals(TriggerChannel, nextTrigger))
            TriggerChannel = nextTrigger;
    }

    private static void SyncObservableCollection<T>(
        ObservableCollection<T> target,
        IList<T> desired,
        Func<T, string> keySelector)
    {
        var desiredKeys = new HashSet<string>(desired.Select(keySelector), StringComparer.Ordinal);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(keySelector(target[i])))
                target.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            string key = keySelector(item);
            int existingIndex = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (string.Equals(keySelector(target[j]), key, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(i, item);
            }
            else if (existingIndex != i)
            {
                target.Move(existingIndex, i);
            }
        }
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
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusMessage = $"已导出 CSV: {dlg.FileName}";
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        StopMonitoring("ViewModel 销毁");
    }
}
