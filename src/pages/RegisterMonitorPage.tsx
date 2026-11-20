import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { RefreshCw, Play, Square, Activity, Table2, Columns2 } from 'lucide-react';
import type {
  ModbusTransport,
  RegisterByteOrder,
  RegisterDataType,
} from '../core/types';
import { ByteUtils } from '../core/utils/byteUtils';
import { ModbusFrame } from '../protocol/modbus/modbusFrame';
import { RegisterValueConverter } from '../protocol/modbus/registerValueConverter';
import { useAppContext } from '../context/AppContext';
import {
  OscilloscopeCanvas,
  SCOPE_CHANNEL_COLORS,
  type ScopeChannelConfig,
  type ScopeSample,
} from '../components/OscilloscopeCanvas';

interface RegisterRow {
  channelId: string;
  label: string;
  address: number;
  rawValue: string;
  value: number;
  isDigital: boolean;
  note: string;
}

type MonitorSource = 'polling' | 'server';
type ViewLayout = 'split' | 'scope' | 'table';
type MonitorFunctionCode = 1 | 2 | 3 | 4;

const DATA_TYPE_OPTIONS: RegisterDataType[] = [
  'Bool',
  'Int16',
  'UInt16',
  'Int32',
  'UInt32',
  'Float',
  'Double',
];
const BYTE_ORDER_OPTIONS: RegisterByteOrder[] = [
  'BigEndian',
  'LittleEndian',
  'WordSwap',
  'ByteSwap',
];

const BYTE_ORDER_LABELS: Record<RegisterByteOrder, string> = {
  BigEndian: 'ABCD 大端序',
  LittleEndian: 'DCBA 小端序',
  WordSwap: 'CDAB 字交换（64位字逆序）',
  ByteSwap: 'BADC 字节交换',
};
const MAX_SCOPE_SAMPLES = 600;

export const RegisterMonitorPage: React.FC = () => {
  const { status, settings } = useAppContext();

  const [monitorSource, setMonitorSource] = useState<MonitorSource>('polling');
  const [viewLayout, setViewLayout] = useState<ViewLayout>('split');
  const [functionCode, setFunctionCode] = useState<MonitorFunctionCode>(3);
  const [slaveId, setSlaveId] = useState<number>(1);
  const [startAddress, setStartAddress] = useState<number>(0);
  const [registerCount, setRegisterCount] = useState<number>(6);
  const [pollIntervalMs, setPollIntervalMs] = useState<number>(500);
  const [selectedDataType, setSelectedDataType] =
    useState<RegisterDataType>('UInt16');
  const [selectedByteOrder, setSelectedByteOrder] =
    useState<RegisterByteOrder>('BigEndian');
  const [scale, setScale] = useState<number>(1);
  const [isPolling, setIsPolling] = useState<boolean>(false);
  const [isBusy, setIsBusy] = useState<boolean>(false);
  const [statusText, setStatusText] = useState<string>('未开始采集');
  const [rows, setRows] = useState<RegisterRow[]>([]);
  const [notesByKey, setNotesByKey] = useState<Record<string, string>>({});

  // Oscilloscope state
  const [scopeSamples, setScopeSamples] = useState<ScopeSample[]>([]);
  const [isFrozen, setIsFrozen] = useState<boolean>(false);
  const [timeWindowSec, setTimeWindowSec] = useState<number>(60);
  const [channelOverrides, setChannelOverrides] = useState<Record<string, boolean>>({});

  const inFlightRef = useRef(false);
  const notesRef = useRef(notesByKey);
  notesRef.current = notesByKey;
  const isFrozenRef = useRef(isFrozen);
  isFrozenRef.current = isFrozen;

  const isBitFunctionCode = functionCode === 1 || functionCode === 2;

  const appendSample = useCallback((values: Record<string, number>) => {
    if (isFrozenRef.current) return;
    const timestampMs = Date.now();
    setScopeSamples((prev) =>
      [...prev, { timestampMs, values }].slice(-MAX_SCOPE_SAMPLES)
    );
  }, []);

  // Build rows & sample from built-in Modbus server registers
  const captureServerSnapshot = useCallback(() => {
    const defs = status.serverRegisters;
    const nextRows: RegisterRow[] = [];
    const sampleValues: Record<string, number> = {};

    for (const def of defs) {
      const channelId = `srv:${def.area}:${def.address}`;
      const rawText = def.currentValue ?? def.initialValue;
      const isDigital = def.dataType === 'Bit';
      let numeric = 0;
      if (isDigital) {
        const normalized = rawText.trim().toLowerCase();
        numeric = normalized === 'true' || normalized === '1' ? 1 : 0;
      } else {
        const parsed = Number(rawText);
        numeric = Number.isFinite(parsed) ? parsed : 0;
      }
      sampleValues[channelId] = numeric;
      nextRows.push({
        channelId,
        label: `${def.area} #${def.address}`,
        address: def.address,
        rawValue: isDigital ? (numeric === 1 ? 'true (1)' : 'false (0)') : rawText,
        value: numeric,
        isDigital,
        note: notesRef.current[channelId] ?? def.note ?? '',
      });
    }

    setRows(nextRows);
    if (nextRows.length > 0) {
      appendSample(sampleValues);
      setStatusText(`实时监听内置服务器，共 ${nextRows.length} 个通道`);
    } else {
      setStatusText('内置服务器暂无已定义的寄存器');
    }
  }, [status.serverRegisters, appendSample]);

  // Trigger server snapshot automatically whenever serverRegisters updates in 'server' mode
  useEffect(() => {
    if (monitorSource !== 'server') return;
    captureServerSnapshot();
  }, [monitorSource, captureServerSnapshot]);

  const readOncePolling = useCallback(async () => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    setIsBusy(true);
    try {
      const transport: ModbusTransport =
        status.channelType === 'TcpClient'
          ? 'Tcp'
          : status.isServerRunning && status.serverMode === 'SerialAscii'
            ? 'Ascii'
            : 'Rtu';
      const maxQty = isBitFunctionCode ? 2000 : 125;
      const qty = Math.min(Math.max(1, Math.floor(registerCount) || 1), maxQty);
      const txId = Math.floor(Math.random() * 65535) + 1;
      const request = ModbusFrame.buildRequest(
        slaveId,
        functionCode,
        startAddress,
        qty,
        transport,
        txId
      );
      const res = await fetch('/api/modbus/exchange', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          requestHex: ByteUtils.bytesToHex(request),
          timeoutMs: settings.defaultTimeoutMs,
          retries: settings.defaultRetryTimes,
          transport,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        setStatusText(`读取失败：${data.error || '通讯错误'}`);
        return;
      }

      const response = ModbusFrame.parseResponse(
        ByteUtils.hexToBytes(data.responseHex),
        transport
      );
      if (response.isException) {
        setStatusText(
          `读取失败：Modbus 异常码 0x${(response.exceptionCode ?? 0).toString(16).toUpperCase().padStart(2, '0')}`
        );
        return;
      }

      const nextRows: RegisterRow[] = [];
      const sampleValues: Record<string, number> = {};

      if (isBitFunctionCode) {
        const bits = ModbusFrame.extractBits(response.payload, qty);
        const prefix = functionCode === 1 ? 'Coil' : 'DI';
        for (let i = 0; i < bits.length; i++) {
          const addr = startAddress + i;
          const bitVal = bits[i] ? 1 : 0;
          const channelId = `bit:${functionCode}:${addr}`;
          sampleValues[channelId] = bitVal;
          nextRows.push({
            channelId,
            label: `${prefix} #${addr}`,
            address: addr,
            rawValue: bits[i] ? '1 (true)' : '0 (false)',
            value: bitVal,
            isDigital: true,
            note: notesRef.current[channelId] ?? '',
          });
        }
        setRows(nextRows);
        if (Object.keys(sampleValues).length > 0) {
          appendSample(sampleValues);
        }
        setStatusText(
          `采集成功，${bits.length} 个布尔变量 (${new Date().toLocaleTimeString()})`
        );
        return;
      }

      const values = response.registers;
      const width = RegisterValueConverter.getWidth(selectedDataType);
      const isBoolWord = selectedDataType === 'Bool';

      for (let i = 0; i + width <= values.length; i += width) {
        const addr = startAddress + i;
        const channelId = `reg:${functionCode}:${addr}`;
        const converted = RegisterValueConverter.convert(
          values.slice(i, i + width),
          selectedDataType,
          selectedByteOrder,
          isBoolWord ? 1 : scale
        );
        if (Number.isFinite(converted)) {
          sampleValues[channelId] = converted;
        }
        const hexWord = `0x${values[i].toString(16).toUpperCase().padStart(4, '0')}`;
        nextRows.push({
          channelId,
          label: `Addr ${addr}`,
          address: addr,
          rawValue: `${values[i]} (${hexWord})`,
          value: converted,
          isDigital: isBoolWord,
          note: notesRef.current[channelId] ?? '',
        });
      }

      setRows(nextRows);
      if (Object.keys(sampleValues).length > 0) {
        appendSample(sampleValues);
      }
      setStatusText(
        `采集成功，${values.length} 个寄存器 (${new Date().toLocaleTimeString()})`
      );
    } catch (err) {
      setStatusText(
        `读取失败：${err instanceof Error ? err.message : '读取异常'}`
      );
    } finally {
      inFlightRef.current = false;
      setIsBusy(false);
    }
  }, [
    status.channelType,
    status.isServerRunning,
    status.serverMode,
    isBitFunctionCode,
    slaveId,
    functionCode,
    startAddress,
    registerCount,
    selectedDataType,
    selectedByteOrder,
    scale,
    settings.defaultTimeoutMs,
    settings.defaultRetryTimes,
    appendSample,
  ]);

  const handleSampleOnce = () => {
    if (monitorSource === 'server') {
      captureServerSnapshot();
    } else {
      void readOncePolling();
    }
  };

  useEffect(() => {
    if (!isPolling) return;
    const interval = Math.max(100, pollIntervalMs);
    if (monitorSource === 'server') {
      captureServerSnapshot();
      const timer = window.setInterval(() => {
        captureServerSnapshot();
      }, interval);
      return () => window.clearInterval(timer);
    }

    void readOncePolling();
    const timer = window.setInterval(() => {
      void readOncePolling();
    }, interval);
    return () => window.clearInterval(timer);
  }, [isPolling, pollIntervalMs, monitorSource, readOncePolling, captureServerSnapshot]);

  const scopeChannels: ScopeChannelConfig[] = useMemo(() => {
    return rows.map((row, idx) => {
      const enabled =
        row.channelId in channelOverrides
          ? channelOverrides[row.channelId]
          : idx < 6;
      return {
        id: row.channelId,
        label: row.note ? `${row.label} (${row.note})` : row.label,
        color: SCOPE_CHANNEL_COLORS[idx % SCOPE_CHANNEL_COLORS.length],
        enabled,
        isDigital: row.isDigital,
      };
    });
  }, [rows, channelOverrides]);

  const handleToggleChannel = (channelId: string) => {
    const current = scopeChannels.find((c) => c.id === channelId)?.enabled ?? true;
    setChannelOverrides((prev) => ({ ...prev, [channelId]: !current }));
  };

  const handleNoteChange = (channelId: string, note: string) => {
    setNotesByKey((prev) => ({ ...prev, [channelId]: note }));
    setRows((prev) =>
      prev.map((row) => (row.channelId === channelId ? { ...row, note } : row))
    );
  };

  const handleSourceSwitch = (next: MonitorSource) => {
    setIsPolling(false);
    setMonitorSource(next);
    setScopeSamples([]);
    setIsFrozen(false);
  };

  const handleFunctionCodeChange = (nextFn: MonitorFunctionCode) => {
    setFunctionCode(nextFn);
    if ((nextFn === 3 || nextFn === 4) && registerCount > 125) {
      setRegisterCount(125);
    }
  };

  return (
    <div className="h-full overflow-y-auto p-6 space-y-4">
      {/* 标题与视图切换 */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">数据监听与示波器中心</h1>
          <p className="text-xs text-[var(--text-secondary)] mt-1">
            支持线圈/离散输入布尔方波与数值寄存器多通道示波器追踪、触发检测与列表监视。
          </p>
        </div>

        <div className="flex items-center gap-2">
          {/* 采集数据源切换 */}
          <div className="flex items-center gap-1 p-1 rounded-md bg-[var(--surface-secondary)] border border-[var(--border-subtle)] text-xs">
            <button
              type="button"
              onClick={() => handleSourceSwitch('polling')}
              className={`px-2.5 py-1 rounded transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                monitorSource === 'polling'
                  ? 'bg-[var(--surface-card)] font-semibold text-[var(--text-primary)] shadow-xs'
                  : 'text-[var(--text-secondary)] hover:text-[var(--text-primary)]'
              }`}
            >
              Modbus 轮询采样
            </button>
            <button
              type="button"
              onClick={() => handleSourceSwitch('server')}
              className={`px-2.5 py-1 rounded transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                monitorSource === 'server'
                  ? 'bg-[var(--surface-card)] font-semibold text-[var(--text-primary)] shadow-xs'
                  : 'text-[var(--text-secondary)] hover:text-[var(--text-primary)]'
              }`}
            >
              内置服务器实时监听
            </button>
          </div>

          {/* 布局模式切换 */}
          <div className="flex items-center gap-1 p-1 rounded-md bg-[var(--surface-secondary)] border border-[var(--border-subtle)] text-xs">
            <button
              type="button"
              onClick={() => setViewLayout('split')}
              title="分屏视图"
              className={`inline-flex items-center gap-1 px-2 py-1 rounded transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                viewLayout === 'split'
                  ? 'bg-[var(--surface-card)] font-semibold text-[var(--text-primary)] shadow-xs'
                  : 'text-[var(--text-secondary)] hover:text-[var(--text-primary)]'
              }`}
            >
              <Columns2 className="w-3.5 h-3.5" />
              <span>分屏</span>
            </button>
            <button
              type="button"
              onClick={() => setViewLayout('scope')}
              title="仅示波器视图"
              className={`inline-flex items-center gap-1 px-2 py-1 rounded transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                viewLayout === 'scope'
                  ? 'bg-[var(--surface-card)] font-semibold text-[var(--text-primary)] shadow-xs'
                  : 'text-[var(--text-secondary)] hover:text-[var(--text-primary)]'
              }`}
            >
              <Activity className="w-3.5 h-3.5" />
              <span>示波器</span>
            </button>
            <button
              type="button"
              onClick={() => setViewLayout('table')}
              title="仅数据列表"
              className={`inline-flex items-center gap-1 px-2 py-1 rounded transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                viewLayout === 'table'
                  ? 'bg-[var(--surface-card)] font-semibold text-[var(--text-primary)] shadow-xs'
                  : 'text-[var(--text-secondary)] hover:text-[var(--text-primary)]'
              }`}
            >
              <Table2 className="w-3.5 h-3.5" />
              <span>列表</span>
            </button>
          </div>
        </div>
      </div>

      {/* 采样控制面板 */}
      <div className="fluent-card p-4 space-y-3">
        <div className="flex flex-wrap items-end gap-3">
          {monitorSource === 'polling' && (
            <>
              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  功能码 / 变量区
                </label>
                <select
                  value={functionCode}
                  onChange={(e) =>
                    handleFunctionCodeChange(
                      Number(e.target.value) as MonitorFunctionCode
                    )
                  }
                  className="fluent-input w-44"
                >
                  <option value={1}>01 读线圈 (Bool)</option>
                  <option value={2}>02 读离散输入 (Bool)</option>
                  <option value={3}>03 读保持寄存器</option>
                  <option value={4}>04 读输入寄存器</option>
                </select>
              </div>

              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  从站 ID
                </label>
                <input
                  type="number"
                  min={1}
                  max={247}
                  value={slaveId}
                  onChange={(e) => setSlaveId(Number(e.target.value))}
                  className="fluent-input w-20"
                />
              </div>

              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  起始地址
                </label>
                <input
                  type="number"
                  min={0}
                  max={65535}
                  value={startAddress}
                  onChange={(e) => setStartAddress(Number(e.target.value))}
                  className="fluent-input w-24"
                />
              </div>

              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  数量
                </label>
                <input
                  type="number"
                  min={1}
                  max={isBitFunctionCode ? 2000 : 125}
                  value={registerCount}
                  onChange={(e) => setRegisterCount(Number(e.target.value))}
                  className="fluent-input w-20"
                />
              </div>

              {!isBitFunctionCode && (
                <>
                  <div>
                    <label className="block text-xs text-[var(--text-secondary)] mb-1">
                      数据类型
                    </label>
                    <select
                      value={selectedDataType}
                      onChange={(e) =>
                        setSelectedDataType(e.target.value as RegisterDataType)
                      }
                      className="fluent-input w-28"
                    >
                      {DATA_TYPE_OPTIONS.map((dt) => (
                        <option key={dt} value={dt}>
                          {dt}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div>
                    <label className="block text-xs text-[var(--text-secondary)] mb-1">
                      字节序
                    </label>
                    <select
                      value={selectedByteOrder}
                      onChange={(e) =>
                        setSelectedByteOrder(e.target.value as RegisterByteOrder)
                      }
                      className="fluent-input w-28"
                    >
                      {BYTE_ORDER_OPTIONS.map((bo) => (
                        <option key={bo} value={bo}>
                          {BYTE_ORDER_LABELS[bo]}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div>
                    <label className="block text-xs text-[var(--text-secondary)] mb-1">
                      缩放
                    </label>
                    <input
                      type="number"
                      step="any"
                      disabled={selectedDataType === 'Bool'}
                      value={selectedDataType === 'Bool' ? 1 : scale}
                      onChange={(e) => setScale(Number(e.target.value))}
                      className="fluent-input w-20"
                    />
                  </div>
                </>
              )}
            </>
          )}

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              采样周期 (ms)
            </label>
            <input
              type="number"
              min={100}
              step={100}
              value={pollIntervalMs}
              onChange={(e) => setPollIntervalMs(Number(e.target.value))}
              className="fluent-input w-24"
            />
          </div>

          <button
            type="button"
            disabled={isBusy}
            onClick={handleSampleOnce}
            className="fluent-btn h-[34px] whitespace-nowrap shrink-0"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${isBusy ? 'animate-spin' : ''}`} />
            <span>{monitorSource === 'server' ? '采集一帧' : '读取一次'}</span>
          </button>

          <button
            type="button"
            onClick={() => setIsPolling((p) => !p)}
            className={`fluent-btn h-[34px] whitespace-nowrap shrink-0 ${
              isPolling ? 'fluent-btn-danger' : 'fluent-btn-primary'
            }`}
          >
            {isPolling ? (
              <>
                <Square className="w-3.5 h-3.5" />
                <span>停止采集</span>
              </>
            ) : (
              <>
                <Play className="w-3.5 h-3.5" />
                <span>{monitorSource === 'server' ? '连续监听' : '开始轮询'}</span>
              </>
            )}
          </button>
        </div>

        <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-[var(--text-secondary)]">
          <span>{statusText}</span>
          {monitorSource === 'server' && (
            <span>
              提示：在「通讯连接」中修改服务器线圈/寄存器或由外部主站写入时，波形将自动实时触发采样
            </span>
          )}
        </div>
      </div>

      {/* 示波器面板 */}
      {viewLayout !== 'table' && (
        <div className="fluent-card p-4">
          <OscilloscopeCanvas
            channels={scopeChannels}
            samples={scopeSamples}
            isFrozen={isFrozen}
            onToggleFreeze={() => setIsFrozen((v) => !v)}
            onClearSamples={() => setScopeSamples([])}
            onToggleChannel={handleToggleChannel}
            timeWindowSec={timeWindowSec}
            onTimeWindowChange={setTimeWindowSec}
          />
        </div>
      )}

      {/* 寄存器与布尔变量实时数值列表 */}
      {viewLayout !== 'scope' && (
        <div className="fluent-card p-4">
          <div className="border border-[var(--border-subtle)] rounded overflow-x-auto min-h-[200px]">
            <table className="w-full text-xs text-left border-collapse">
              <thead className="bg-[var(--surface-secondary)] border-b border-[var(--border-subtle)]">
                <tr>
                  <th className="px-3 py-2 font-semibold w-20">波形</th>
                  <th className="px-3 py-2 font-semibold w-40">通道 / 地址</th>
                  <th className="px-3 py-2 font-semibold w-40">原始值</th>
                  <th className="px-3 py-2 font-semibold w-36">实时数值</th>
                  <th className="px-3 py-2 font-semibold">备注</th>
                </tr>
              </thead>
              <tbody>
                {rows.length === 0 ? (
                  <tr>
                    <td
                      colSpan={5}
                      className="px-3 py-8 text-center text-[var(--text-secondary)]"
                    >
                      暂无监视数据，请点击“读取一次” / “开始轮询”或切换至“内置服务器实时监听”
                    </td>
                  </tr>
                ) : (
                  rows.map((row, idx) => {
                    const ch = scopeChannels[idx];
                    const enabled = ch?.enabled ?? true;
                    const color = ch?.color ?? SCOPE_CHANNEL_COLORS[0];
                    const isBoolOn = row.value >= 0.5;
                    return (
                      <tr
                        key={row.channelId}
                        className="border-b border-[var(--border-subtle)] last:border-b-0 hover:bg-[var(--surface-hover)]"
                      >
                        <td className="px-3 py-2">
                          <button
                            type="button"
                            onClick={() => handleToggleChannel(row.channelId)}
                            className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded border text-[11px] cursor-pointer ${
                              enabled
                                ? 'border-[var(--border-subtle)] bg-[var(--surface-secondary)] font-semibold'
                                : 'border-[var(--border-subtle)] opacity-45'
                            }`}
                          >
                            <span
                              className="w-2 h-2 rounded-xs inline-block"
                              style={{ backgroundColor: color }}
                            />
                            <span>{enabled ? 'CH' : '关'}</span>
                          </button>
                        </td>
                        <td className="px-3 py-2 tabular-nums font-medium">
                          {row.label}
                        </td>
                        <td className="px-3 py-2 font-mono-code tabular-nums">
                          {row.rawValue}
                        </td>
                        <td className="px-3 py-2 font-mono-code font-semibold tabular-nums">
                          {row.isDigital ? (
                            <span
                              className={
                                isBoolOn
                                  ? 'text-emerald-600 dark:text-emerald-400'
                                  : 'text-[var(--text-secondary)]'
                              }
                            >
                              {isBoolOn ? 'ON (1)' : 'OFF (0)'}
                            </span>
                          ) : (
                            <span className="text-[var(--accent-primary)]">
                              {row.value}
                            </span>
                          )}
                        </td>
                        <td className="px-3 py-1.5">
                          <input
                            type="text"
                            value={row.note}
                            onChange={(e) =>
                              handleNoteChange(row.channelId, e.target.value)
                            }
                            placeholder="添加备注 (同步至波形图例)..."
                            className="fluent-input py-1 w-full max-w-xs"
                          />
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
};
