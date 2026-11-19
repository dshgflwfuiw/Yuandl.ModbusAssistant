import React, { useEffect, useRef, useState } from 'react';
import {
  PlugZap,
  Unplug,
  RefreshCw,
  Play,
  Square,
  Plus,
  Trash2,
  Upload,
  Download,
  Check,
  UserX,
  RotateCcw,
} from 'lucide-react';
import type {
  ModbusServerConfiguration,
  ModbusServerTransportMode,
  ParityType,
  SerialPortParameters,
  ServerRegisterArea,
  ServerRegisterByteOrder,
  ServerRegisterDataType,
  ServerRegisterDefinition,
  StopBitsType,
} from '../core/types';
import { ServerRegisterCodec } from '../protocol/modbus/serverRegisterCodec';
import { useAppContext } from '../context/AppContext';

const BAUD_RATES = [
  1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 230400, 460800, 921600,
];
const DATA_BITS = [7, 8];
const PARITIES: ParityType[] = ['None', 'Odd', 'Even', 'Mark', 'Space'];
const STOP_BITS: StopBitsType[] = ['One', 'Two', 'OnePointFive'];

const REGISTER_AREAS: ServerRegisterArea[] = [
  'HoldingRegister',
  'InputRegister',
  'Coil',
  'DiscreteInput',
];
const REGISTER_DATA_TYPES: ServerRegisterDataType[] = [
  'Bit',
  'Int16',
  'UInt16',
  'Int32',
  'UInt32',
  'Float',
  'Double',
];
const REGISTER_BYTE_ORDERS: ServerRegisterByteOrder[] = [
  'BigEndian',
  'LittleEndian',
  'WordSwap',
  'ByteSwap',
];

const BYTE_ORDER_LABELS: Record<ServerRegisterByteOrder, string> = {
  BigEndian: 'ABCD 大端序',
  LittleEndian: 'DCBA 小端序',
  WordSwap: 'CDAB 字交换',
  ByteSwap: 'BADC 字节交换',
};

export const ConnectionPage: React.FC = () => {
  const {
    status,
    commForm,
    setCommForm,
    connectChannel,
    disconnectChannel,
    disconnectTcpServerClient,
    showSnackbar,
    refreshState,
    persistServerConfig,
  } = useAppContext();

  const [availablePorts, setAvailablePorts] = useState<string[]>([
    'COM1',
    'COM2',
    'COM3',
    '/dev/ttyUSB0',
  ]);
  const [isBusy, setIsBusy] = useState(false);

  // Modbus Server state (TCP & Serial)
  const [serverMode, setServerMode] = useState<ModbusServerTransportMode>(
    status.serverMode || 'Tcp'
  );
  const [serverPort, setServerPort] = useState<number>(status.serverPort || 5020);
  const [serverStation, setServerStation] = useState<number>(status.serverStation || 1);
  const [serverSerial, setServerSerial] = useState<SerialPortParameters>(
    status.serverSerial || {
      portName: 'COM1',
      baudRate: 9600,
      dataBits: 8,
      parity: 'None',
      stopBits: 'One',
      dtrEnable: true,
      rtsEnable: true,
    }
  );
  const [selectedRegKey, setSelectedRegKey] = useState<string | null>(null);
  const [regArea, setRegArea] = useState<ServerRegisterArea>('HoldingRegister');
  const [regAddress, setRegAddress] = useState<number>(0);
  const [regDataType, setRegDataType] = useState<ServerRegisterDataType>('UInt16');
  const [regByteOrder, setRegByteOrder] = useState<ServerRegisterByteOrder>('BigEndian');
  const [regValue, setRegValue] = useState<string>('0');
  const [regNote, setRegNote] = useState<string>('');
  const [runtimeValue, setRuntimeValue] = useState<string>('0');
  const [inlineDrafts, setInlineDrafts] = useState<Record<string, string>>({});
  const [savingRegKey, setSavingRegKey] = useState<string | null>(null);

  const fileInputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (status.serverPort) setServerPort(status.serverPort);
    if (status.serverStation) setServerStation(status.serverStation);
    if (status.serverMode) setServerMode(status.serverMode);
    if (status.serverSerial) setServerSerial(status.serverSerial);
  }, [status.serverPort, status.serverStation, status.serverMode, status.serverSerial]);

  const handleRefreshPorts = () => {
    setAvailablePorts(['COM1', 'COM2', 'COM3', '/dev/ttyUSB0']);
    showSnackbar('端口刷新', '已更新可用串口列表 (容器内为仿真串口)', 'info');
  };

  const handleConnect = async () => {
    if (status.isConnected || isBusy) return;
    setIsBusy(true);
    try {
      if (commForm.selectedCommTypeIndex === 0) {
        await connectChannel({
          type: 'SerialPort',
          serial: {
            portName: commForm.selectedPort,
            baudRate: commForm.selectedBaudRate,
            dataBits: commForm.dataBits,
            parity: commForm.selectedParity,
            stopBits: commForm.selectedStopBits,
            dtrEnable: commForm.dtrEnable,
            rtsEnable: commForm.rtsEnable,
          },
        });
      } else if (commForm.selectedCommTypeIndex === 1) {
        await connectChannel({
          type: 'TcpClient',
          tcp: {
            host: commForm.tcpHost,
            port: commForm.tcpPort,
            connectTimeoutMs: commForm.connectTimeoutMs,
            autoReconnect: commForm.autoReconnect,
          },
        });
      } else if (commForm.selectedCommTypeIndex === 2) {
        await connectChannel({
          type: 'Udp',
          udp: {
            remoteHost: commForm.udpRemoteHost,
            remotePort: commForm.udpRemotePort,
            localPort: commForm.udpLocalPort,
          },
        });
      } else {
        await connectChannel({
          type: 'TcpServer',
          tcpServer: {
            listenPort: commForm.tcpServerPort,
          },
        });
      }
    } finally {
      setIsBusy(false);
    }
  };

  const handleDisconnect = async () => {
    if (isBusy) return;
    setIsBusy(true);
    try {
      await disconnectChannel();
    } finally {
      setIsBusy(false);
    }
  };

  const handleAreaChange = (area: ServerRegisterArea) => {
    setRegArea(area);
    const isBit = area === 'Coil' || area === 'DiscreteInput';
    if (isBit) {
      setRegDataType('Bit');
      if (regValue !== 'true' && regValue !== 'false') {
        setRegValue('false');
      }
    } else if (regDataType === 'Bit') {
      setRegDataType('UInt16');
      if (regValue === 'true' || regValue === 'false') {
        setRegValue('0');
      }
    }
  };

  const syncLocalServerConfig = (
    port: number,
    station: number,
    regs: ServerRegisterDefinition[],
    mode: ModbusServerTransportMode = serverMode,
    serial: SerialPortParameters = serverSerial
  ) => {
    persistServerConfig({
      version: 1,
      port,
      station,
      serverMode: mode,
      serverSerial: serial,
      registers: regs.map(({ currentValue: _cv, ...rest }) => rest),
    });
  };

  const handleStartServer = async () => {
    try {
      const res = await fetch('/api/server/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          port: serverPort,
          station: serverStation,
          mode: serverMode,
          serial: serverSerial,
        }),
      });
      const data = await res.json();
      await refreshState();
      if (!res.ok) {
        showSnackbar('服务器启动失败', data.error || '无法启动服务器', 'error');
        return;
      }
      syncLocalServerConfig(
        serverPort,
        serverStation,
        data.status?.serverRegisters ?? status.serverRegisters,
        serverMode,
        serverSerial
      );
      showSnackbar(
        '服务器已启动',
        data.status?.serverStatusText || `Modbus 服务器已就绪 (站号 ${serverStation})`,
        'success'
      );
    } catch (err) {
      showSnackbar(
        '服务器启动失败',
        err instanceof Error ? err.message : '请求失败',
        'error'
      );
    }
  };

  const handleStopServer = async () => {
    await fetch('/api/server/stop', { method: 'POST' });
    await refreshState();
    showSnackbar('服务器已停止', 'Modbus 服务器已关闭', 'info');
  };

  const handleCreateOrUpdateRegister = async () => {
    try {
      const def: ServerRegisterDefinition = {
        area: regArea,
        address: regAddress,
        dataType: regDataType,
        byteOrder: regByteOrder,
        initialValue: regValue,
        note: regNote,
      };
      ServerRegisterCodec.validate(def);
      const res = await fetch('/api/server/registers', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(def),
      });
      const data = await res.json();
      if (!res.ok) {
        showSnackbar('建立数据定义失败', data.error || '校验失败', 'error');
        return;
      }
      if (data.status?.serverRegisters) {
        syncLocalServerConfig(
          serverPort,
          serverStation,
          data.status.serverRegisters
        );
      }
      await refreshState();
      setSelectedRegKey(`${def.area}:${def.address}`);
      setRuntimeValue(def.initialValue);
      showSnackbar(
        '数据定义已保存',
        `${def.area} 地址 ${def.address} = ${def.initialValue}`,
        'success'
      );
    } catch (err) {
      showSnackbar(
        '建立数据定义失败',
        err instanceof Error ? err.message : '输入无效',
        'error'
      );
    }
  };

  const applyRuntimeValueFor = async (
    target: {
      area: ServerRegisterArea;
      address: number;
      dataType: ServerRegisterDataType;
      byteOrder: ServerRegisterByteOrder;
    },
    rawVal: string
  ) => {
    const key = `${target.area}:${target.address}`;
    setSavingRegKey(key);
    try {
      if (target.dataType === 'Bit') {
        ServerRegisterCodec.encodeBit(rawVal);
      } else {
        ServerRegisterCodec.encodeRegisters(
          rawVal,
          target.dataType,
          target.byteOrder
        );
      }

      const res = await fetch('/api/server/registers/runtime', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          area: target.area,
          address: target.address,
          value: rawVal,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        showSnackbar('写入当前值失败', data.error || '输入格式错误', 'error');
        return;
      }
      setInlineDrafts((prev) => {
        if (!(key in prev)) return prev;
        const next = { ...prev };
        delete next[key];
        return next;
      });
      if (selectedRegKey === key) {
        setRuntimeValue(rawVal);
      }
      await refreshState();
      showSnackbar(
        '运行值已更新',
        `${target.area} 地址 ${target.address} 当前值 = ${rawVal}`,
        'success'
      );
    } catch (err) {
      showSnackbar(
        '写入当前值失败',
        err instanceof Error ? err.message : '操作异常',
        'error'
      );
    } finally {
      setSavingRegKey((prev) => (prev === key ? null : prev));
    }
  };

  const handleApplyRuntimeValue = async () => {
    await applyRuntimeValueFor(
      {
        area: regArea,
        address: regAddress,
        dataType: regDataType,
        byteOrder: regByteOrder,
      },
      runtimeValue
    );
  };

  const handleResetAllRuntimeValues = async () => {
    try {
      const res = await fetch('/api/server/registers/reset', {
        method: 'POST',
      });
      const data = await res.json();
      if (!res.ok) {
        showSnackbar('重置失败', data.error || '操作异常', 'error');
        return;
      }
      setInlineDrafts({});
      await refreshState();
      showSnackbar('已重置', '所有服务器寄存器已恢复为初始值', 'info');
    } catch (err) {
      showSnackbar(
        '重置失败',
        err instanceof Error ? err.message : '操作异常',
        'error'
      );
    }
  };

  const handleDeleteSelected = async () => {
    if (!selectedRegKey) return;
    const [area, addrStr] = selectedRegKey.split(':');
    const res = await fetch(
      `/api/server/registers?area=${encodeURIComponent(area)}&address=${encodeURIComponent(addrStr)}`,
      { method: 'DELETE' }
    );
    const data = await res.json();
    if (!res.ok) {
      showSnackbar('删除数据定义失败', data.error || '删除失败', 'error');
      return;
    }
    if (data.status?.serverRegisters) {
      syncLocalServerConfig(
        serverPort,
        serverStation,
        data.status.serverRegisters
      );
    }
    setSelectedRegKey(null);
    await refreshState();
  };

  const handleSelectRow = (item: ServerRegisterDefinition) => {
    const key = `${item.area}:${item.address}`;
    setSelectedRegKey(key);
    setRegArea(item.area);
    setRegAddress(item.address);
    setRegDataType(item.dataType);
    setRegByteOrder(item.byteOrder);
    setRegValue(item.initialValue);
    setRegNote(item.note);
    setRuntimeValue(item.currentValue ?? item.initialValue);
  };

  const handleExportJson = () => {
    const payload: ModbusServerConfiguration = {
      version: 1,
      port: serverPort,
      station: serverStation,
      serverMode,
      serverSerial,
      registers: status.serverRegisters.map(
        ({ currentValue: _cv, ...rest }) => rest
      ),
    };
    const blob = new Blob([JSON.stringify(payload, null, 2)], {
      type: 'application/json',
    });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'modbus-server.json';
    a.click();
    URL.revokeObjectURL(url);
    showSnackbar('导出成功', '已导出 modbus-server.json', 'success');
  };

  const handleImportJson = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    try {
      const text = await file.text();
      const parsed = JSON.parse(text) as ModbusServerConfiguration;
      const res = await fetch('/api/server/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(parsed),
      });
      const data = await res.json();
      if (!res.ok) {
        showSnackbar('配置加载失败', data.error || '格式错误', 'error');
        return;
      }
      const nextMode = parsed.serverMode ?? serverMode;
      const nextSerial = parsed.serverSerial ?? serverSerial;
      setServerPort(parsed.port);
      setServerStation(parsed.station);
      if (parsed.serverMode) setServerMode(parsed.serverMode);
      if (parsed.serverSerial) setServerSerial(parsed.serverSerial);
      syncLocalServerConfig(
        parsed.port,
        parsed.station,
        parsed.registers,
        nextMode,
        nextSerial
      );
      await refreshState();
      showSnackbar(
        '配置加载成功',
        `已加载 ${parsed.registers.length} 项数据定义`,
        'success'
      );
    } catch (err) {
      showSnackbar(
        '配置加载失败',
        err instanceof Error ? err.message : 'JSON 解析失败',
        'error'
      );
    } finally {
      e.target.value = '';
    }
  };

  return (
    <div className="h-full overflow-y-auto p-6 space-y-4">
      {/* 1. 标题区 */}
      <div>
        <h1 className="text-xl font-semibold text-[var(--text-primary)]">
          通讯链路管理
        </h1>
        <p className="text-xs text-[var(--text-secondary)] mt-1">
          配置串行端口 (RS232/RS485)、TCP 客户端、UDP 通讯及无协议 TCP 服务端监听链路
        </p>
      </div>

      {/* 2. 主配置区域 */}
      <div className="fluent-card p-5 space-y-4">
        <div>
          <h2 className="text-sm font-semibold mb-2.5">通讯介质类型</h2>
          <select
            value={commForm.selectedCommTypeIndex}
            disabled={status.isConnected}
            onChange={(e) =>
              setCommForm((prev) => ({
                ...prev,
                selectedCommTypeIndex: Number(e.target.value),
              }))
            }
            className="fluent-input w-72"
          >
            <option value={0}>RS232 / RS485 串行端口</option>
            <option value={1}>TCP Client (客户端)</option>
            <option value={2}>UDP 通讯 (无连接)</option>
            <option value={3}>TCP Server (无协议监听)</option>
          </select>
        </div>

        <hr className="border-[var(--border-subtle)]" />

        {/* 串口参数 (Index = 0) */}
        {commForm.selectedCommTypeIndex === 0 && (
          <div className="space-y-3">
            <div className="flex items-center justify-between flex-wrap gap-2">
              <span className="text-xs font-semibold">串口参数设定</span>
              <span className="text-[11px] text-[var(--text-secondary)]">
                Web 容器环境下自动以虚拟串口环回/Modbus RTU 仿真通道运行
              </span>
            </div>
            <div className="flex flex-wrap gap-4">
              <div className="w-56">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  COM 端口
                </label>
                <div className="flex gap-1.5">
                  <select
                    value={commForm.selectedPort}
                    disabled={status.isConnected}
                    onChange={(e) =>
                      setCommForm((p) => ({ ...p, selectedPort: e.target.value }))
                    }
                    className="fluent-input flex-1"
                  >
                    {availablePorts.map((p) => (
                      <option key={p} value={p}>
                        {p}
                      </option>
                    ))}
                  </select>
                  <button
                    type="button"
                    onClick={handleRefreshPorts}
                    disabled={status.isConnected}
                    title="刷新可用串口"
                    className="fluent-btn px-2.5"
                  >
                    <RefreshCw className="w-3.5 h-3.5" />
                  </button>
                </div>
              </div>

              <div className="w-40">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  波特率 (Baud Rate)
                </label>
                <select
                  value={commForm.selectedBaudRate}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({
                      ...p,
                      selectedBaudRate: Number(e.target.value),
                    }))
                  }
                  className="fluent-input w-full"
                >
                  {BAUD_RATES.map((b) => (
                    <option key={b} value={b}>
                      {b}
                    </option>
                  ))}
                </select>
              </div>

              <div className="w-32">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  数据位 (Data Bits)
                </label>
                <select
                  value={commForm.dataBits}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({ ...p, dataBits: Number(e.target.value) }))
                  }
                  className="fluent-input w-full"
                >
                  {DATA_BITS.map((d) => (
                    <option key={d} value={d}>
                      {d}
                    </option>
                  ))}
                </select>
              </div>

              <div className="w-36">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  校验位 (Parity)
                </label>
                <select
                  value={commForm.selectedParity}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({
                      ...p,
                      selectedParity: e.target.value as ParityType,
                    }))
                  }
                  className="fluent-input w-full"
                >
                  {PARITIES.map((par) => (
                    <option key={par} value={par}>
                      {par}
                    </option>
                  ))}
                </select>
              </div>

              <div className="w-36">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  停止位 (Stop Bits)
                </label>
                <select
                  value={commForm.selectedStopBits}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({
                      ...p,
                      selectedStopBits: e.target.value as StopBitsType,
                    }))
                  }
                  className="fluent-input w-full"
                >
                  {STOP_BITS.map((sb) => (
                    <option key={sb} value={sb}>
                      {sb}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <div className="flex flex-wrap gap-6 pt-1 text-xs">
              <label className="inline-flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={commForm.dtrEnable}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({ ...p, dtrEnable: e.target.checked }))
                  }
                />
                <span>启用 DTR (数据终端就绪)</span>
              </label>
              <label className="inline-flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={commForm.rtsEnable}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({ ...p, rtsEnable: e.target.checked }))
                  }
                />
                <span>启用 RTS (请求发送 - RS485流控)</span>
              </label>
            </div>
          </div>
        )}

        {/* TCP Client 参数 (Index = 1) */}
        {commForm.selectedCommTypeIndex === 1 && (
          <div className="space-y-3">
            <div className="text-xs font-semibold">TCP 客户端参数设定</div>
            <div className="flex flex-wrap gap-4">
              <div className="w-60">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  目标主机 IP / 域名
                </label>
                <input
                  type="text"
                  value={commForm.tcpHost}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({ ...p, tcpHost: e.target.value }))
                  }
                  className="fluent-input w-full"
                />
              </div>
              <div className="w-40">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  目标端口 (Port)
                </label>
                <input
                  type="number"
                  value={commForm.tcpPort}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({ ...p, tcpPort: Number(e.target.value) }))
                  }
                  className="fluent-input w-full"
                />
              </div>
              <div className="w-44">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  连接超时时间 (ms)
                </label>
                <input
                  type="number"
                  value={commForm.connectTimeoutMs}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({
                      ...p,
                      connectTimeoutMs: Number(e.target.value),
                    }))
                  }
                  className="fluent-input w-full"
                />
              </div>
            </div>
            <label className="inline-flex items-center gap-2 text-xs cursor-pointer">
              <input
                type="checkbox"
                checked={commForm.autoReconnect}
                disabled={status.isConnected}
                onChange={(e) =>
                  setCommForm((p) => ({ ...p, autoReconnect: e.target.checked }))
                }
              />
              <span>开启异常断线自动重连 (3秒间隔)</span>
            </label>
          </div>
        )}

        {/* UDP 参数 (Index = 2) */}
        {commForm.selectedCommTypeIndex === 2 && (
          <div className="space-y-3">
            <div className="text-xs font-semibold">UDP 通讯参数设定</div>
            <div className="flex flex-wrap gap-4">
              <div className="w-60">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  远程目标主机 IP
                </label>
                <input
                  type="text"
                  value={commForm.udpRemoteHost}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({ ...p, udpRemoteHost: e.target.value }))
                  }
                  className="fluent-input w-full"
                />
              </div>
              <div className="w-40">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  远程端口 (Port)
                </label>
                <input
                  type="number"
                  value={commForm.udpRemotePort}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({
                      ...p,
                      udpRemotePort: Number(e.target.value),
                    }))
                  }
                  className="fluent-input w-full"
                />
              </div>
              <div className="w-48">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  本地绑定端口 (0为自动)
                </label>
                <input
                  type="number"
                  value={commForm.udpLocalPort}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({
                      ...p,
                      udpLocalPort: Number(e.target.value),
                    }))
                  }
                  className="fluent-input w-full"
                />
              </div>
            </div>
          </div>
        )}

        {/* TCP Server 参数 (Index = 3) */}
        {commForm.selectedCommTypeIndex === 3 && (
          <div className="space-y-3">
            <div className="flex items-center justify-between flex-wrap gap-2">
              <span className="text-xs font-semibold">
                无协议 TCP Server 监听设定
              </span>
              <span className="text-[11px] text-[var(--text-secondary)]">
                支持多客户端同时连接，在「无协议调试」中按客户端会话定向收发
              </span>
            </div>
            <div className="flex flex-wrap gap-4">
              <div className="w-48">
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  本地监听端口 (Port)
                </label>
                <input
                  type="number"
                  min={1}
                  max={65535}
                  value={commForm.tcpServerPort}
                  disabled={status.isConnected}
                  onChange={(e) =>
                    setCommForm((p) => ({
                      ...p,
                      tcpServerPort: Number(e.target.value),
                    }))
                  }
                  className="fluent-input w-full"
                />
              </div>
            </div>

            {status.channelType === 'TcpServer' && status.isConnected && (
              <div className="space-y-2 pt-2">
                <div className="text-xs font-semibold">
                  在线客户端会话 ({status.tcpServerClients.length})
                </div>
                <div className="border border-[var(--border-subtle)] rounded overflow-x-auto max-h-44">
                  <table className="w-full text-xs text-left border-collapse">
                    <thead className="bg-[var(--surface-secondary)] border-b border-[var(--border-subtle)]">
                      <tr>
                        <th className="px-3 py-1.5 font-semibold w-20">会话</th>
                        <th className="px-3 py-1.5 font-semibold">远端地址</th>
                        <th className="px-3 py-1.5 font-semibold">接入时间</th>
                        <th className="px-3 py-1.5 font-semibold">TX / RX</th>
                        <th className="px-3 py-1.5 font-semibold w-24">操作</th>
                      </tr>
                    </thead>
                    <tbody>
                      {status.tcpServerClients.length === 0 ? (
                        <tr>
                          <td
                            colSpan={5}
                            className="px-3 py-4 text-center text-[var(--text-secondary)]"
                          >
                            正在监听连接，暂无客户端接入...
                          </td>
                        </tr>
                      ) : (
                        status.tcpServerClients.map((client) => (
                          <tr
                            key={client.sessionId}
                            className="border-b border-[var(--border-subtle)] last:border-b-0"
                          >
                            <td className="px-3 py-1.5 font-mono-code font-semibold">
                              {client.sessionId}
                            </td>
                            <td className="px-3 py-1.5 font-mono-code">
                              {client.remoteAddress}:{client.remotePort}
                            </td>
                            <td className="px-3 py-1.5 tabular-nums text-[var(--text-secondary)]">
                              {new Date(client.connectedAt).toLocaleTimeString()}
                            </td>
                            <td className="px-3 py-1.5 tabular-nums">
                              {client.bytesSent} B / {client.bytesReceived} B
                            </td>
                            <td className="px-3 py-1">
                              <button
                                type="button"
                                onClick={() =>
                                  void disconnectTcpServerClient(client.sessionId)
                                }
                                className="fluent-btn py-0.5 px-2 text-[11px]"
                              >
                                <UserX className="w-3 h-3" />
                                <span>断开</span>
                              </button>
                            </td>
                          </tr>
                        ))
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* 3. 操作控制与状态呈现 */}
      <div className="fluent-card p-5 flex flex-wrap items-center justify-between gap-4">
        <div>
          <div className="text-sm font-semibold flex items-center gap-2">
            <span>当前状态:</span>
            <span
              className={
                status.isConnected
                  ? 'text-[var(--status-connected)]'
                  : 'text-[var(--status-disconnected)]'
              }
            >
              {status.stateText}
            </span>
          </div>
          <p className="text-xs text-[var(--text-secondary)] mt-1">
            连接或监听启动后，可在 Modbus 调试、无协议调试、寄存器监视与报文分析中协同工作
          </p>
        </div>

        <div className="flex items-center gap-3">
          <button
            type="button"
            disabled={status.isConnected || isBusy}
            onClick={handleConnect}
            className="fluent-btn fluent-btn-primary h-9 min-w-[120px] whitespace-nowrap shrink-0"
          >
            <PlugZap className="w-4 h-4" />
            <span>
              {commForm.selectedCommTypeIndex === 3 ? '启动监听' : '建立连接'}
            </span>
          </button>
          <button
            type="button"
            disabled={!status.isConnected || isBusy}
            onClick={handleDisconnect}
            className="fluent-btn fluent-btn-danger h-9 min-w-[120px] whitespace-nowrap shrink-0"
          >
            <Unplug className="w-4 h-4" />
            <span>
              {status.channelType === 'TcpServer' ? '停止监听' : '断开连接'}
            </span>
          </button>
        </div>
      </div>

      {/* 4. Modbus 服务器模式 (TCP / 串口) */}
      <div className="fluent-card p-5 space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold">Modbus 服务器模式 (TCP / 串口)</h2>
          <span className="text-[11px] text-[var(--text-secondary)]">
            串口从站支持 RTU / ASCII；连接上方相同串口号与参数即可与本服务器联调
          </span>
        </div>

        <div className="flex flex-wrap items-end gap-3">
          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              服务器通道模式
            </label>
            <select
              value={serverMode}
              disabled={status.isServerRunning}
              onChange={(e) =>
                setServerMode(e.target.value as ModbusServerTransportMode)
              }
              className="fluent-input w-44"
            >
              <option value="Tcp">Modbus TCP</option>
              <option value="SerialRtu">Modbus RTU (串口)</option>
              <option value="SerialAscii">Modbus ASCII (串口)</option>
              <option value="TcpAndSerial">TCP + 串口双通道</option>
            </select>
          </div>

          {(serverMode === 'Tcp' || serverMode === 'TcpAndSerial') && (
            <div>
              <label className="block text-xs text-[var(--text-secondary)] mb-1">
                TCP 监听端口
              </label>
              <input
                type="number"
                value={serverPort}
                disabled={status.isServerRunning}
                onChange={(e) => setServerPort(Number(e.target.value))}
                className="fluent-input w-28"
              />
            </div>
          )}

          {serverMode !== 'Tcp' && (
            <>
              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  服务器串口号
                </label>
                <select
                  value={serverSerial.portName}
                  disabled={status.isServerRunning}
                  onChange={(e) =>
                    setServerSerial((p) => ({ ...p, portName: e.target.value }))
                  }
                  className="fluent-input w-36"
                >
                  {availablePorts.map((p) => (
                    <option key={p} value={p}>
                      {p}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  波特率
                </label>
                <select
                  value={serverSerial.baudRate}
                  disabled={status.isServerRunning}
                  onChange={(e) =>
                    setServerSerial((p) => ({
                      ...p,
                      baudRate: Number(e.target.value),
                    }))
                  }
                  className="fluent-input w-28"
                >
                  {BAUD_RATES.map((b) => (
                    <option key={b} value={b}>
                      {b}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  数据位
                </label>
                <select
                  value={serverSerial.dataBits}
                  disabled={status.isServerRunning}
                  onChange={(e) =>
                    setServerSerial((p) => ({
                      ...p,
                      dataBits: Number(e.target.value),
                    }))
                  }
                  className="fluent-input w-20"
                >
                  {DATA_BITS.map((d) => (
                    <option key={d} value={d}>
                      {d}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  校验位
                </label>
                <select
                  value={serverSerial.parity}
                  disabled={status.isServerRunning}
                  onChange={(e) =>
                    setServerSerial((p) => ({
                      ...p,
                      parity: e.target.value as ParityType,
                    }))
                  }
                  className="fluent-input w-24"
                >
                  {PARITIES.map((par) => (
                    <option key={par} value={par}>
                      {par}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-xs text-[var(--text-secondary)] mb-1">
                  停止位
                </label>
                <select
                  value={serverSerial.stopBits}
                  disabled={status.isServerRunning}
                  onChange={(e) =>
                    setServerSerial((p) => ({
                      ...p,
                      stopBits: e.target.value as StopBitsType,
                    }))
                  }
                  className="fluent-input w-28"
                >
                  {STOP_BITS.map((sb) => (
                    <option key={sb} value={sb}>
                      {sb}
                    </option>
                  ))}
                </select>
              </div>
            </>
          )}

          <div>
            <label className="block text-xs text-[var(--text-secondary)] mb-1">
              站号
            </label>
            <input
              type="number"
              value={serverStation}
              disabled={status.isServerRunning}
              onChange={(e) => setServerStation(Number(e.target.value))}
              className="fluent-input w-20"
            />
          </div>
          <button
            type="button"
            disabled={status.isServerRunning}
            onClick={handleStartServer}
            className="fluent-btn fluent-btn-primary h-[34px] whitespace-nowrap shrink-0"
          >
            <Play className="w-3.5 h-3.5" />
            <span>启动服务器</span>
          </button>
          <button
            type="button"
            disabled={!status.isServerRunning}
            onClick={handleStopServer}
            className="fluent-btn h-[34px] whitespace-nowrap shrink-0"
          >
            <Square className="w-3.5 h-3.5" />
            <span>停止服务器</span>
          </button>
        </div>
        <div className="text-xs text-[var(--text-secondary)]">
          {status.serverStatusText}
        </div>
      </div>

      {/* 5. 服务器数据区配置 */}
      <div className="fluent-card p-5 space-y-3">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div>
            <h2 className="text-sm font-semibold">服务器数据区配置</h2>
            <p className="text-[11px] text-[var(--text-secondary)] mt-0.5">
              支持在下方表格中直接在线修改实时值（位类型单击切换，数值类型输入后按 Enter 或点击“写入”生效）
            </p>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => void handleResetAllRuntimeValues()}
              disabled={status.serverRegisters.length === 0}
              className="fluent-btn text-xs"
            >
              <RotateCcw className="w-3.5 h-3.5" />
              <span>重置初始值</span>
            </button>
            <input
              ref={fileInputRef}
              type="file"
              accept=".json,application/json"
              className="hidden"
              onChange={handleImportJson}
            />
            <button
              type="button"
              onClick={() => fileInputRef.current?.click()}
              className="fluent-btn text-xs"
            >
              <Upload className="w-3.5 h-3.5" />
              <span>导入 JSON</span>
            </button>
            <button
              type="button"
              onClick={handleExportJson}
              className="fluent-btn text-xs"
            >
              <Download className="w-3.5 h-3.5" />
              <span>导出 JSON</span>
            </button>
          </div>
        </div>

        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label className="block text-[11px] text-[var(--text-secondary)] mb-1">
              数据区
            </label>
            <select
              value={regArea}
              onChange={(e) => handleAreaChange(e.target.value as ServerRegisterArea)}
              className="fluent-input w-36"
            >
              {REGISTER_AREAS.map((area) => (
                <option key={area} value={area}>
                  {area}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-[11px] text-[var(--text-secondary)] mb-1">
              起始地址
            </label>
            <input
              type="number"
              min={0}
              max={65535}
              value={regAddress}
              onChange={(e) => setRegAddress(Number(e.target.value))}
              className="fluent-input w-24"
            />
          </div>

          <div>
            <label className="block text-[11px] text-[var(--text-secondary)] mb-1">
              数据类型
            </label>
            <select
              value={regDataType}
              onChange={(e) => setRegDataType(e.target.value as ServerRegisterDataType)}
              className="fluent-input w-28"
            >
              {REGISTER_DATA_TYPES.filter((dt) =>
                regArea === 'Coil' || regArea === 'DiscreteInput'
                  ? dt === 'Bit'
                  : dt !== 'Bit'
              ).map((dt) => (
                <option key={dt} value={dt}>
                  {dt}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-[11px] text-[var(--text-secondary)] mb-1">
              字节序
            </label>
            <select
              value={regByteOrder}
              onChange={(e) =>
                setRegByteOrder(e.target.value as ServerRegisterByteOrder)
              }
              className="fluent-input w-28"
            >
              {REGISTER_BYTE_ORDERS.map((bo) => (
                <option key={bo} value={bo}>
                  {BYTE_ORDER_LABELS[bo]}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-[11px] text-[var(--text-secondary)] mb-1">
              初始值 (Bit: true/false)
            </label>
            <input
              type="text"
              value={regValue}
              onChange={(e) => setRegValue(e.target.value)}
              className="fluent-input w-28"
            />
          </div>

          <div>
            <label className="block text-[11px] text-[var(--text-secondary)] mb-1">
              备注
            </label>
            <input
              type="text"
              value={regNote}
              onChange={(e) => setRegNote(e.target.value)}
              className="fluent-input w-36"
            />
          </div>

          <button
            type="button"
            onClick={handleCreateOrUpdateRegister}
            className="fluent-btn fluent-btn-primary h-[34px]"
          >
            <Plus className="w-3.5 h-3.5" />
            <span>添加/更新</span>
          </button>

          <button
            type="button"
            disabled={!selectedRegKey}
            onClick={handleDeleteSelected}
            className="fluent-btn h-[34px]"
          >
            <Trash2 className="w-3.5 h-3.5" />
            <span>删除所选</span>
          </button>
        </div>

        {/* 在线修改当前运行值 */}
        {selectedRegKey && (
          <div className="flex flex-wrap items-center gap-2 p-2.5 rounded bg-[var(--surface-secondary)] border border-[var(--border-subtle)] text-xs">
            <span className="font-medium">
              已选寄存器快捷修改 ({regArea} @ {regAddress}):
            </span>
            <input
              type="text"
              value={runtimeValue}
              onChange={(e) => setRuntimeValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  void handleApplyRuntimeValue();
                }
              }}
              className="fluent-input w-36 py-1 font-mono-code"
            />
            <button
              type="button"
              onClick={() => void handleApplyRuntimeValue()}
              className="fluent-btn fluent-btn-primary py-1"
            >
              <Check className="w-3.5 h-3.5" />
              <span>应用当前值</span>
            </button>
          </div>
        )}

        <div className="border border-[var(--border-subtle)] rounded overflow-x-auto max-h-80 overflow-y-auto">
          <table className="w-full text-xs text-left border-collapse">
            <thead className="bg-[var(--surface-secondary)] sticky top-0 z-10 border-b border-[var(--border-subtle)]">
              <tr>
                <th className="px-3 py-2 font-semibold">区域</th>
                <th className="px-3 py-2 font-semibold">地址</th>
                <th className="px-3 py-2 font-semibold">类型</th>
                <th className="px-3 py-2 font-semibold">字节序</th>
                <th className="px-3 py-2 font-semibold">长度</th>
                <th className="px-3 py-2 font-semibold">初始值</th>
                <th className="px-3 py-2 font-semibold min-w-[210px]">
                  当前值 (在线修改)
                </th>
                <th className="px-3 py-2 font-semibold">备注</th>
              </tr>
            </thead>
            <tbody>
              {status.serverRegisters.length === 0 ? (
                <tr>
                  <td
                    colSpan={8}
                    className="px-3 py-6 text-center text-[var(--text-secondary)]"
                  >
                    暂无寄存器定义，请在上方添加数据区配置
                  </td>
                </tr>
              ) : (
                status.serverRegisters.map((item) => {
                  const key = `${item.area}:${item.address}`;
                  const isSelected = selectedRegKey === key;
                  const liveValue = item.currentValue ?? item.initialValue;
                  const hasDraft = key in inlineDrafts;
                  const draftValue = hasDraft ? inlineDrafts[key] : liveValue;
                  const isDirty = hasDraft && draftValue !== liveValue;
                  const isBit = item.dataType === 'Bit';
                  const isBitOn = liveValue.trim().toLowerCase() === 'true';
                  const isRowSaving = savingRegKey === key;

                  return (
                    <tr
                      key={key}
                      onClick={() => handleSelectRow(item)}
                      className={`cursor-pointer border-b border-[var(--border-subtle)] last:border-b-0 ${
                        isSelected
                          ? 'bg-[var(--surface-selected)]'
                          : 'hover:bg-[var(--surface-hover)]'
                      }`}
                    >
                      <td className="px-3 py-2">{item.area}</td>
                      <td className="px-3 py-2 tabular-nums">{item.address}</td>
                      <td className="px-3 py-2">{item.dataType}</td>
                      <td className="px-3 py-2">{item.byteOrder}</td>
                      <td className="px-3 py-2 tabular-nums">
                        {ServerRegisterCodec.getWidth(item.dataType)}
                      </td>
                      <td className="px-3 py-2 font-mono-code text-[var(--text-secondary)]">
                        {item.initialValue}
                      </td>
                      <td
                        className="px-3 py-1.5"
                        onClick={(e) => e.stopPropagation()}
                      >
                        {isBit ? (
                          <button
                            type="button"
                            disabled={isRowSaving}
                            onClick={() =>
                              void applyRuntimeValueFor(
                                item,
                                isBitOn ? 'false' : 'true'
                              )
                            }
                            title="单击立即切换线圈/离散输入状态"
                            className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded text-[11px] font-mono-code font-semibold border transition-colors ${
                              isBitOn
                                ? 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-300 border-emerald-500/40 hover:bg-emerald-500/25'
                                : 'bg-[var(--surface-secondary)] text-[var(--text-secondary)] border-[var(--border-subtle)] hover:bg-[var(--surface-hover)]'
                            }`}
                          >
                            <span
                              className={`w-2 h-2 rounded-full ${
                                isBitOn ? 'bg-emerald-500' : 'bg-zinc-400'
                              }`}
                            />
                            <span>{isBitOn ? 'ON (true)' : 'OFF (false)'}</span>
                          </button>
                        ) : (
                          <div className="flex items-center gap-1.5">
                            <input
                              type="text"
                              value={draftValue}
                              disabled={isRowSaving}
                              onChange={(e) => {
                                const val = e.target.value;
                                setInlineDrafts((prev) => ({
                                  ...prev,
                                  [key]: val,
                                }));
                              }}
                              onKeyDown={(e) => {
                                if (e.key === 'Enter') {
                                  void applyRuntimeValueFor(item, draftValue);
                                } else if (e.key === 'Escape') {
                                  setInlineDrafts((prev) => {
                                    const next = { ...prev };
                                    delete next[key];
                                    return next;
                                  });
                                }
                              }}
                              className={`fluent-input w-28 py-1 px-2 text-xs font-mono-code font-semibold ${
                                isDirty
                                  ? 'border-[var(--accent-primary)] ring-1 ring-[var(--accent-primary)]/30'
                                  : 'text-[var(--accent-primary)]'
                              }`}
                            />
                            <button
                              type="button"
                              disabled={isRowSaving}
                              onClick={() =>
                                void applyRuntimeValueFor(item, draftValue)
                              }
                              title="立即写入当前值到 Modbus 服务器"
                              className={`fluent-btn py-1 px-2 text-[11px] whitespace-nowrap ${
                                isDirty ? 'fluent-btn-primary' : ''
                              }`}
                            >
                              <Check className="w-3 h-3" />
                              <span>写入</span>
                            </button>
                          </div>
                        )}
                      </td>
                      <td className="px-3 py-2 text-[var(--text-secondary)]">
                        {item.note}
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
};
