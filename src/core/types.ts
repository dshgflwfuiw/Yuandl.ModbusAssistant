export type ChannelState =
  | 'Disconnected'
  | 'Connecting'
  | 'Connected'
  | 'Reconnecting'
  | 'Faulted';

export type CommunicationType = 'SerialPort' | 'TcpClient' | 'Udp' | 'TcpServer';

export type DataEncoding = 'Hex' | 'Ascii' | 'Utf8';

export type EndingDelimiter = 'None' | 'CR' | 'LF' | 'CRLF';

export type ParityType = 'None' | 'Odd' | 'Even' | 'Mark' | 'Space';

export type StopBitsType = 'One' | 'Two' | 'OnePointFive';

export type ModbusTransport = 'Rtu' | 'Ascii' | 'Tcp';

export type RegisterDataType = 'Bool' | 'Int16' | 'UInt16' | 'Int32' | 'UInt32' | 'Float' | 'Double';

export type RegisterByteOrder = 'BigEndian' | 'LittleEndian' | 'WordSwap' | 'ByteSwap';

export type ServerRegisterArea =
  | 'HoldingRegister'
  | 'InputRegister'
  | 'Coil'
  | 'DiscreteInput';

export type ServerRegisterDataType =
  | 'Bit'
  | 'Int16'
  | 'UInt16'
  | 'Int32'
  | 'UInt32'
  | 'Float'
  | 'Double';

export type ServerRegisterByteOrder = 'BigEndian' | 'LittleEndian' | 'WordSwap' | 'ByteSwap';

export interface SerialPortParameters {
  portName: string;
  baudRate: number;
  dataBits: number;
  parity: ParityType;
  stopBits: StopBitsType;
  dtrEnable: boolean;
  rtsEnable: boolean;
  readTimeoutMs?: number;
  writeTimeoutMs?: number;
}

export interface TcpClientParameters {
  host: string;
  port: number;
  connectTimeoutMs: number;
  autoReconnect: boolean;
  readTimeoutMs?: number;
  writeTimeoutMs?: number;
  reconnectIntervalMs?: number;
}

export interface UdpParameters {
  remoteHost: string;
  remotePort: number;
  localPort: number;
}

export interface TcpServerParameters {
  listenPort: number;
}

export interface TcpServerClientSession {
  sessionId: string;
  remoteAddress: string;
  remotePort: number;
  connectedAt: string;
  bytesSent: number;
  bytesReceived: number;
}

export interface ServerRegisterDefinition {
  area: ServerRegisterArea;
  address: number;
  dataType: ServerRegisterDataType;
  byteOrder: ServerRegisterByteOrder;
  initialValue: string;
  note: string;
  currentValue?: string;
}

export type ModbusServerTransportMode =
  | 'Tcp'
  | 'SerialRtu'
  | 'SerialAscii'
  | 'TcpAndSerial';

export interface ModbusServerConfiguration {
  version: number;
  port: number;
  station: number;
  serverMode?: ModbusServerTransportMode;
  serverSerial?: SerialPortParameters;
  registers: ServerRegisterDefinition[];
}

export interface ChannelStatistics {
  bytesSent: number;
  bytesReceived: number;
  packetsSent: number;
  packetsReceived: number;
  errorsCount: number;
}

export interface DeviceProfile {
  id: string;
  name: string;
  protocol: string;
  endpoint: string;
  stationId: number;
}

export interface AppSettings {
  theme: 'light' | 'dark';
  defaultTimeoutMs: number;
  defaultRetryTimes: number;
}

export interface TrafficEvent {
  direction: 'TX' | 'RX';
  bytes: number[];
  hex: string;
  timestamp: string;
  epochMs: number;
  channelType: CommunicationType | null;
  sessionId?: string;
  sessionLabel?: string;
}

export interface CommunicationStatusSnapshot {
  isConnected: boolean;
  state: ChannelState;
  stateText: string;
  communicationType: string;
  channelType: CommunicationType | null;
  statistics: ChannelStatistics;
  deviceInfo: string;
  tcpServerClients: TcpServerClientSession[];
  isServerRunning: boolean;
  serverMode: ModbusServerTransportMode;
  serverPort: number;
  serverStation: number;
  serverSerial: SerialPortParameters;
  serverStatusText: string;
  serverRegisters: ServerRegisterDefinition[];
}

export type NavPageId =
  | 'connection'
  | 'modbus-debug'
  | 'raw-debug'
  | 'register-monitor'
  | 'packet-analysis'
  | 'device-management'
  | 'settings';
