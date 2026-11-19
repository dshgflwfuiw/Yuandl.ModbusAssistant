import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
} from 'react';
import type {
  AppSettings,
  CommunicationStatusSnapshot,
  DeviceProfile,
  ModbusServerConfiguration,
  ModbusTransport,
  NavPageId,
  SerialPortParameters,
  TcpClientParameters,
  TcpServerParameters,
  TrafficEvent,
  UdpParameters,
} from '../core/types';
import { ModbusFrame } from '../protocol/modbus/modbusFrame';
import { ModbusException } from '../protocol/modbus/modbusException';

export type SnackbarSeverity = 'info' | 'success' | 'warning' | 'error';

export interface SnackbarMessage {
  id: string;
  title: string;
  message: string;
  severity: SnackbarSeverity;
}

export interface IncomingDataEvent {
  bytes: Uint8Array;
  hex: string;
  timestamp: string;
  sessionId?: string;
  sessionLabel?: string;
}

export interface AnalyzedPacketEntry {
  id: string;
  timestamp: string;
  direction: 'TX' | 'RX' | 'ERR';
  transport: ModbusTransport;
  hex: string;
  result: string;
  rttMs?: number;
  sessionLabel?: string;
}

export interface PacketAnalysisStats {
  totalPackets: number;
  errorPackets: number;
  matchedPairs: number;
  avgRttMs: number | null;
  minRttMs: number | null;
  maxRttMs: number | null;
}

const STORAGE_KEYS = {
  settings: 'yuandl.modbus.settings.v1',
  profiles: 'yuandl.modbus.profiles.v1',
  serverConfig: 'yuandl.modbus.serverConfig.v1',
  commForm: 'yuandl.modbus.commForm.v1',
};

const DEFAULT_COMM_FORM = {
  selectedCommTypeIndex: 0,
  selectedPort: 'COM1',
  selectedBaudRate: 9600,
  dataBits: 8,
  selectedParity: 'None' as SerialPortParameters['parity'],
  selectedStopBits: 'One' as SerialPortParameters['stopBits'],
  dtrEnable: true,
  rtsEnable: true,
  tcpHost: '127.0.0.1',
  tcpPort: 5020,
  connectTimeoutMs: 3000,
  autoReconnect: false,
  udpRemoteHost: '127.0.0.1',
  udpRemotePort: 502,
  udpLocalPort: 0,
  tcpServerPort: 6000,
};

function readStorage<T>(key: string): T | null {
  try {
    const raw = window.localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: unknown): void {
  try {
    window.localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // ignore quota errors
  }
}

interface AppContextValue {
  activePage: NavPageId;
  setActivePage: (page: NavPageId) => void;
  status: CommunicationStatusSnapshot;
  profiles: DeviceProfile[];
  settings: AppSettings;
  snackbars: SnackbarMessage[];
  showSnackbar: (title: string, message: string, severity?: SnackbarSeverity) => void;
  dismissSnackbar: (id: string) => void;
  toggleTheme: () => void;
  updateSettings: (next: AppSettings) => Promise<void>;
  refreshState: () => Promise<void>;
  persistServerConfig: (config: ModbusServerConfiguration) => void;
  connectChannel: (payload: {
    type: 'SerialPort' | 'TcpClient' | 'Udp' | 'TcpServer';
    serial?: SerialPortParameters;
    tcp?: TcpClientParameters;
    udp?: UdpParameters;
    tcpServer?: TcpServerParameters;
  }) => Promise<boolean>;
  disconnectChannel: () => Promise<void>;
  disconnectTcpServerClient: (sessionId: string) => Promise<void>;
  addProfile: (profile: Omit<DeviceProfile, 'id'>) => Promise<void>;
  removeProfile: (id: string) => Promise<void>;
  reloadProfiles: () => Promise<void>;
  subscribeDataReceived: (listener: (evt: IncomingDataEvent) => void) => () => void;
  // Packet analysis real-time capture state (persists across tab switches)
  isPacketCaptureEnabled: boolean;
  setIsPacketCaptureEnabled: (enabled: boolean) => void;
  packetCaptureTransport: ModbusTransport | 'Auto';
  setPacketCaptureTransport: (t: ModbusTransport | 'Auto') => void;
  packetEntries: AnalyzedPacketEntry[];
  packetStats: PacketAnalysisStats;
  analyzeManualPacket: (hex: string, transport: ModbusTransport) => string;
  clearPacketEntries: () => void;
  commForm: typeof DEFAULT_COMM_FORM;
  setCommForm: React.Dispatch<React.SetStateAction<typeof DEFAULT_COMM_FORM>>;
}

const DEFAULT_STATUS: CommunicationStatusSnapshot = {
  isConnected: false,
  state: 'Disconnected',
  stateText: '未连接',
  communicationType: '无活动连接',
  channelType: null,
  statistics: {
    bytesSent: 0,
    bytesReceived: 0,
    packetsSent: 0,
    packetsReceived: 0,
    errorsCount: 0,
  },
  deviceInfo: '就绪',
  tcpServerClients: [],
  isServerRunning: false,
  serverMode: 'Tcp',
  serverPort: 5020,
  serverStation: 1,
  serverSerial: {
    portName: 'COM1',
    baudRate: 9600,
    dataBits: 8,
    parity: 'None',
    stopBits: 'One',
    dtrEnable: true,
    rtsEnable: true,
  },
  serverStatusText: '服务器已停止',
  serverRegisters: [],
};

const EMPTY_PACKET_STATS: PacketAnalysisStats = {
  totalPackets: 0,
  errorPackets: 0,
  matchedPairs: 0,
  avgRttMs: null,
  minRttMs: null,
  maxRttMs: null,
};

const AppContext = createContext<AppContextValue | null>(null);

export const AppProvider: React.FC<{ children: React.ReactNode }> = ({
  children,
}) => {
  const [activePage, setActivePage] = useState<NavPageId>('connection');
  const [status, setStatus] = useState<CommunicationStatusSnapshot>(DEFAULT_STATUS);
  const [profiles, setProfiles] = useState<DeviceProfile[]>([]);
  const [settings, setSettings] = useState<AppSettings>(() => {
    return (
      readStorage<AppSettings>(STORAGE_KEYS.settings) ?? {
        theme: 'light',
        defaultTimeoutMs: 1000,
        defaultRetryTimes: 2,
      }
    );
  });
  const [snackbars, setSnackbars] = useState<SnackbarMessage[]>([]);
  const dataListenersRef = useRef(new Set<(evt: IncomingDataEvent) => void>());

  const [commForm, setCommForm] = useState<typeof DEFAULT_COMM_FORM>(() => {
    const saved = readStorage<Partial<typeof DEFAULT_COMM_FORM>>(STORAGE_KEYS.commForm);
    return saved ? { ...DEFAULT_COMM_FORM, ...saved } : DEFAULT_COMM_FORM;
  });

  useEffect(() => {
    writeStorage(STORAGE_KEYS.commForm, commForm);
  }, [commForm]);

  // Real-time Packet Analysis capture & RTT correlation state
  const [isPacketCaptureEnabled, setIsPacketCaptureEnabled] = useState<boolean>(true);
  const [packetCaptureTransport, setPacketCaptureTransport] = useState<
    ModbusTransport | 'Auto'
  >('Auto');
  const [packetEntries, setPacketEntries] = useState<AnalyzedPacketEntry[]>([]);
  const [packetStats, setPacketStats] =
    useState<PacketAnalysisStats>(EMPTY_PACKET_STATS);
  const rttListRef = useRef<number[]>([]);
  const pendingReqRef = useRef<
    Array<{ key: string; epochMs: number }>
  >([]);
  const captureEnabledRef = useRef(isPacketCaptureEnabled);
  captureEnabledRef.current = isPacketCaptureEnabled;
  const captureTransportRef = useRef(packetCaptureTransport);
  captureTransportRef.current = packetCaptureTransport;

  const dismissSnackbar = useCallback((id: string) => {
    setSnackbars((prev) => prev.filter((item) => item.id !== id));
  }, []);

  const showSnackbar = useCallback(
    (title: string, message: string, severity: SnackbarSeverity = 'info') => {
      const id = `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
      setSnackbars((prev) => [...prev.slice(-3), { id, title, message, severity }]);
      setTimeout(() => {
        setSnackbars((prev) => prev.filter((item) => item.id !== id));
      }, 4000);
    },
    []
  );

  // Apply light/dark class on documentElement
  useEffect(() => {
    const root = document.documentElement;
    if (settings.theme === 'dark') {
      root.classList.add('dark');
    } else {
      root.classList.remove('dark');
    }
    writeStorage(STORAGE_KEYS.settings, settings);
  }, [settings]);

  const persistServerConfig = useCallback((config: ModbusServerConfiguration) => {
    writeStorage(STORAGE_KEYS.serverConfig, config);
  }, []);

  const resolveTransport = useCallback(
    (
      channelType: CommunicationStatusSnapshot['channelType'],
      bytes?: Uint8Array
    ): ModbusTransport => {
      const override = captureTransportRef.current;
      if (override !== 'Auto') return override;
      if (channelType === 'TcpClient') return 'Tcp';
      if (
        bytes &&
        bytes.length >= 7 &&
        bytes[0] === 0x3a &&
        bytes[bytes.length - 1] === 0x0a
      ) {
        return 'Ascii';
      }
      return 'Rtu';
    },
    []
  );

  const handleTrafficEvent = useCallback(
    (evt: TrafficEvent) => {
      if (!captureEnabledRef.current) return;
      const bytes = new Uint8Array(evt.bytes ?? []);
      if (bytes.length === 0) return;
      const transport = resolveTransport(evt.channelType, bytes);
      const timeText = new Date(evt.timestamp).toLocaleTimeString();

      if (evt.direction === 'TX') {
        try {
          const parsed = ModbusFrame.parseRequest(bytes, transport);
          const key =
            transport === 'Tcp' && parsed.transactionId !== undefined
              ? `tcp:${parsed.transactionId}:${parsed.unitId}:${parsed.functionCode}`
              : `rtu:${parsed.unitId}:${parsed.functionCode}`;
          pendingReqRef.current = [
            ...pendingReqRef.current.filter((p) => evt.epochMs - p.epochMs < 10000),
            { key, epochMs: evt.epochMs },
          ];
          const valPreview = parsed.values?.length
            ? `，写入值 [${parsed.values.slice(0, 6).join(', ')}${parsed.values.length > 6 ? '...' : ''}]`
            : '';
          const result = `请求 · 地址 ${parsed.unitId} · 功能码 0x${parsed.functionCode.toString(16).toUpperCase().padStart(2, '0')} · 起始 ${parsed.address}${parsed.quantity ? ` · 数量 ${parsed.quantity}` : ''}${valPreview}`;

          setPacketStats((prev) => ({
            ...prev,
            totalPackets: prev.totalPackets + 1,
          }));
          setPacketEntries((prev) =>
            [
              {
                id: `${evt.epochMs}-${Math.random().toString(36).slice(2, 7)}`,
                timestamp: timeText,
                direction: 'TX' as const,
                transport,
                hex: evt.hex,
                result,
                sessionLabel: evt.sessionLabel,
              },
              ...prev,
            ].slice(0, 500)
          );
        } catch {
          // Non-Modbus raw packet in TX
          setPacketStats((prev) => ({
            ...prev,
            totalPackets: prev.totalPackets + 1,
          }));
          setPacketEntries((prev) =>
            [
              {
                id: `${evt.epochMs}-${Math.random().toString(36).slice(2, 7)}`,
                timestamp: timeText,
                direction: 'TX' as const,
                transport,
                hex: evt.hex,
                result: `原始字节发送 (${bytes.length} B)`,
                sessionLabel: evt.sessionLabel,
              },
              ...prev,
            ].slice(0, 500)
          );
        }
        return;
      }

      // RX frame
      try {
        const resp = ModbusFrame.parseResponse(bytes, transport);
        const baseFn = resp.functionCode & 0x7f;
        const matchKey =
          transport === 'Tcp' && resp.transactionId !== undefined
            ? `tcp:${resp.transactionId}:${resp.unitId}:${baseFn}`
            : `rtu:${resp.unitId}:${baseFn}`;
        const matchIdx = pendingReqRef.current.findIndex((p) => p.key === matchKey);
        let rttMs: number | undefined;
        if (matchIdx !== -1) {
          const matched = pendingReqRef.current[matchIdx];
          pendingReqRef.current.splice(matchIdx, 1);
          rttMs = Math.max(0, evt.epochMs - matched.epochMs);
          rttListRef.current.push(rttMs);
        }

        const result = resp.isException
          ? `异常码 0x${(resp.exceptionCode ?? 0).toString(16).toUpperCase().padStart(2, '0')}: ${ModbusException.describe(resp.exceptionCode ?? 0)}`
          : `地址 ${resp.unitId}，功能码 0x${resp.functionCode.toString(16).toUpperCase().padStart(2, '0')}，寄存器 ${resp.registers.length}，CRC/LRC 正确`;

        setPacketStats((prev) => {
          const list = rttListRef.current;
          const matchedPairs = list.length;
          const avgRttMs =
            matchedPairs > 0
              ? Math.round((list.reduce((a, b) => a + b, 0) / matchedPairs) * 10) / 10
              : null;
          const minRttMs = matchedPairs > 0 ? Math.min(...list) : null;
          const maxRttMs = matchedPairs > 0 ? Math.max(...list) : null;
          return {
            totalPackets: prev.totalPackets + 1,
            errorPackets: prev.errorPackets + (resp.isException ? 1 : 0),
            matchedPairs,
            avgRttMs,
            minRttMs,
            maxRttMs,
          };
        });

        setPacketEntries((prev) =>
          [
            {
              id: `${evt.epochMs}-${Math.random().toString(36).slice(2, 7)}`,
              timestamp: timeText,
              direction: 'RX' as const,
              transport,
              hex: evt.hex,
              result,
              rttMs,
              sessionLabel: evt.sessionLabel,
            },
            ...prev,
          ].slice(0, 500)
        );
      } catch (err) {
        const message = err instanceof Error ? err.message : '帧校验失败';
        setPacketStats((prev) => ({
          ...prev,
          totalPackets: prev.totalPackets + 1,
          errorPackets: prev.errorPackets + 1,
        }));
        setPacketEntries((prev) =>
          [
            {
              id: `${evt.epochMs}-${Math.random().toString(36).slice(2, 7)}`,
              timestamp: timeText,
              direction: 'ERR' as const,
              transport,
              hex: evt.hex,
              result: message,
              sessionLabel: evt.sessionLabel,
            },
            ...prev,
          ].slice(0, 500)
        );
      }
    },
    [resolveTransport]
  );

  const analyzeManualPacket = useCallback(
    (hex: string, transport: ModbusTransport): string => {
      try {
        const cleanBytes = new Uint8Array(
          hex
            .replace(/0x/gi, '')
            .replace(/[\s,;-]+/g, '')
            .match(/.{1,2}/g)
            ?.map((b) => Number.parseInt(b, 16)) ?? []
        );
        if (cleanBytes.length === 0) {
          throw new Error('请输入有效的 HEX 报文');
        }
        const response = ModbusFrame.parseResponse(cleanBytes, transport);
        const result = response.isException
          ? `异常码 0x${(response.exceptionCode ?? 0).toString(16).toUpperCase().padStart(2, '0')}: ${ModbusException.describe(response.exceptionCode ?? 0)}`
          : `地址 ${response.unitId}，功能码 0x${response.functionCode.toString(16).toUpperCase().padStart(2, '0')}，寄存器 ${response.registers.length}，CRC/LRC 正确`;

        setPacketStats((prev) => ({
          ...prev,
          totalPackets: prev.totalPackets + 1,
          errorPackets: prev.errorPackets + (response.isException ? 1 : 0),
        }));
        setPacketEntries((prev) =>
          [
            {
              id: `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
              timestamp: new Date().toLocaleTimeString(),
              direction: 'RX' as const,
              transport,
              hex: Array.from(cleanBytes)
                .map((b) => b.toString(16).toUpperCase().padStart(2, '0'))
                .join(' '),
              result,
            },
            ...prev,
          ].slice(0, 500)
        );
        return result;
      } catch (err) {
        const message = err instanceof Error ? err.message : '解析失败';
        setPacketStats((prev) => ({
          ...prev,
          totalPackets: prev.totalPackets + 1,
          errorPackets: prev.errorPackets + 1,
        }));
        setPacketEntries((prev) =>
          [
            {
              id: `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
              timestamp: new Date().toLocaleTimeString(),
              direction: 'ERR' as const,
              transport,
              hex,
              result: message,
            },
            ...prev,
          ].slice(0, 500)
        );
        return message;
      }
    },
    []
  );

  const clearPacketEntries = useCallback(() => {
    setPacketEntries([]);
    setPacketStats(EMPTY_PACKET_STATS);
    rttListRef.current = [];
    pendingReqRef.current = [];
  }, []);

  const refreshState = useCallback(async () => {
    try {
      const res = await fetch('/api/state');
      if (!res.ok) return;
      const data = await res.json();
      if (data.status) setStatus(data.status);
      if (Array.isArray(data.profiles)) setProfiles(data.profiles);
      if (data.settings) setSettings(data.settings);
    } catch {
      // ignore initial offline error
    }
  }, []);

  useEffect(() => {
    const bootstrap = async () => {
      const savedProfiles = readStorage<DeviceProfile[]>(STORAGE_KEYS.profiles);
      const savedSettings = readStorage<AppSettings>(STORAGE_KEYS.settings);
      const savedServerConfig = readStorage<ModbusServerConfiguration>(
        STORAGE_KEYS.serverConfig
      );

      if (savedProfiles || savedSettings || savedServerConfig) {
        try {
          const res = await fetch('/api/state/restore', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              profiles: savedProfiles ?? undefined,
              settings: savedSettings ?? undefined,
              serverConfig: savedServerConfig ?? undefined,
            }),
          });
          if (res.ok) {
            const data = await res.json();
            if (data.status) setStatus(data.status);
            if (Array.isArray(data.profiles)) setProfiles(data.profiles);
            if (data.settings) setSettings(data.settings);
            return;
          }
        } catch {
          // fallback to refreshState
        }
      }
      await refreshState();
    };

    void bootstrap();

    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${window.location.host}/ws`;
    let ws: WebSocket | null = null;
    let closedByUser = false;
    let retryTimer: number | null = null;

    const connectWs = () => {
      ws = new WebSocket(wsUrl);
      ws.onmessage = (event) => {
        try {
          const parsed = JSON.parse(event.data);
          if (parsed.event === 'state' && parsed.payload) {
            setStatus(parsed.payload);
          } else if (parsed.event === 'dataReceived' && parsed.payload) {
            const bytes = new Uint8Array(parsed.payload.bytes ?? []);
            const evt: IncomingDataEvent = {
              bytes,
              hex: parsed.payload.hex ?? '',
              timestamp: parsed.payload.timestamp ?? new Date().toISOString(),
              sessionId: parsed.payload.sessionId,
              sessionLabel: parsed.payload.sessionLabel,
            };
            for (const listener of dataListenersRef.current) {
              listener(evt);
            }
            setStatus((prev) => ({
              ...prev,
              statistics: parsed.payload.statistics ?? prev.statistics,
            }));
          } else if (parsed.event === 'traffic' && parsed.payload) {
            handleTrafficEvent(parsed.payload as TrafficEvent);
          } else if (parsed.event === 'error' && parsed.payload) {
            setStatus((prev) => ({
              ...prev,
              statistics: parsed.payload.statistics ?? prev.statistics,
              deviceInfo: `错误: ${parsed.payload.message}`,
            }));
          }
        } catch {
          // ignore malformed ws message
        }
      };

      ws.onclose = () => {
        if (!closedByUser) {
          retryTimer = window.setTimeout(connectWs, 2000);
        }
      };
    };

    connectWs();

    return () => {
      closedByUser = true;
      if (retryTimer !== null) window.clearTimeout(retryTimer);
      ws?.close();
    };
  }, [refreshState, handleTrafficEvent]);

  const updateSettings = useCallback(async (next: AppSettings) => {
    setSettings(next);
    writeStorage(STORAGE_KEYS.settings, next);
    await fetch('/api/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(next),
    });
  }, []);

  const toggleTheme = useCallback(() => {
    const nextTheme: AppSettings['theme'] =
      settings.theme === 'light' ? 'dark' : 'light';
    void updateSettings({ ...settings, theme: nextTheme });
  }, [settings, updateSettings]);

  const connectChannel = useCallback(
    async (payload: {
      type: 'SerialPort' | 'TcpClient' | 'Udp' | 'TcpServer';
      serial?: SerialPortParameters;
      tcp?: TcpClientParameters;
      udp?: UdpParameters;
      tcpServer?: TcpServerParameters;
    }) => {
      try {
        const res = await fetch('/api/comm/connect', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        });
        const data = await res.json();
        if (data.status) setStatus(data.status);
        if (!res.ok) {
          showSnackbar('连接失败', data.error || '无法建立通讯链路', 'error');
          return false;
        }
        showSnackbar(
          '连接成功',
          `已启动 ${data.status?.communicationType ?? ''}`,
          'success'
        );
        return true;
      } catch (err) {
        const msg = err instanceof Error ? err.message : '连接请求异常';
        showSnackbar('连接失败', msg, 'error');
        return false;
      }
    },
    [showSnackbar]
  );

  const disconnectChannel = useCallback(async () => {
    try {
      const res = await fetch('/api/comm/disconnect', { method: 'POST' });
      const data = await res.json();
      if (data.status) setStatus(data.status);
      showSnackbar('连接已断开', '通讯通道已安全关闭', 'info');
    } catch (err) {
      const msg = err instanceof Error ? err.message : '断开连接异常';
      showSnackbar('断开连接异常', msg, 'warning');
    }
  }, [showSnackbar]);

  const disconnectTcpServerClient = useCallback(
    async (sessionId: string) => {
      const res = await fetch(
        `/api/comm/server-clients/${encodeURIComponent(sessionId)}/disconnect`,
        { method: 'POST' }
      );
      const data = await res.json();
      if (data.status) setStatus(data.status);
      showSnackbar('客户端已断开', `已移除会话 ${sessionId}`, 'info');
    },
    [showSnackbar]
  );

  const addProfile = useCallback(async (profile: Omit<DeviceProfile, 'id'>) => {
    const res = await fetch('/api/profiles', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(profile),
    });
    const data = await res.json();
    if (Array.isArray(data.profiles)) {
      setProfiles(data.profiles);
      writeStorage(STORAGE_KEYS.profiles, data.profiles);
    }
  }, []);

  const removeProfile = useCallback(async (id: string) => {
    const res = await fetch(`/api/profiles/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    });
    const data = await res.json();
    if (Array.isArray(data.profiles)) {
      setProfiles(data.profiles);
      writeStorage(STORAGE_KEYS.profiles, data.profiles);
    }
  }, []);

  const reloadProfiles = useCallback(async () => {
    const res = await fetch('/api/profiles');
    const data = await res.json();
    if (Array.isArray(data.profiles)) {
      setProfiles(data.profiles);
      writeStorage(STORAGE_KEYS.profiles, data.profiles);
    }
  }, []);

  const subscribeDataReceived = useCallback(
    (listener: (evt: IncomingDataEvent) => void) => {
      dataListenersRef.current.add(listener);
      return () => {
        dataListenersRef.current.delete(listener);
      };
    },
    []
  );

  return (
    <AppContext.Provider
      value={{
        activePage,
        setActivePage,
        status,
        profiles,
        settings,
        snackbars,
        showSnackbar,
        dismissSnackbar,
        toggleTheme,
        updateSettings,
        refreshState,
        persistServerConfig,
        connectChannel,
        disconnectChannel,
        disconnectTcpServerClient,
        addProfile,
        removeProfile,
        reloadProfiles,
        subscribeDataReceived,
        isPacketCaptureEnabled,
        setIsPacketCaptureEnabled,
        packetCaptureTransport,
        setPacketCaptureTransport,
        packetEntries,
        packetStats,
        analyzeManualPacket,
        clearPacketEntries,
        commForm,
        setCommForm,
      }}
    >
      {children}
    </AppContext.Provider>
  );
};

export function useAppContext(): AppContextValue {
  const ctx = useContext(AppContext);
  if (!ctx) {
    throw new Error('useAppContext must be used inside AppProvider');
  }
  return ctx;
}
