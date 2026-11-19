import express from 'express';
import http from 'node:http';
import net from 'node:net';
import dgram from 'node:dgram';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocketServer, WebSocket } from 'ws';
import { createServer as createViteServer } from 'vite';
import type {
  AppSettings,
  ChannelState,
  ChannelStatistics,
  CommunicationStatusSnapshot,
  CommunicationType,
  DeviceProfile,
  ModbusServerConfiguration,
  ModbusServerTransportMode,
  SerialPortParameters,
  ServerRegisterArea,
  ServerRegisterDefinition,
  TcpClientParameters,
  TcpServerClientSession,
  TcpServerParameters,
  TrafficEvent,
  UdpParameters,
} from './src/core/types.js';
import { ByteUtils } from './src/core/utils/byteUtils.js';
import { Crc16 } from './src/core/utils/crc16.js';
import { LrcUtils } from './src/core/utils/lrcUtils.js';
import { ServerRegisterCodec } from './src/protocol/modbus/serverRegisterCodec.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = 3000;

class ModbusTcpServerService {
  private readonly holdingRegisters = new Uint16Array(65536);
  private readonly inputRegisters = new Uint16Array(65536);
  private readonly coils = new Uint8Array(65536);
  private readonly discreteInputs = new Uint8Array(65536);

  private definitions: ServerRegisterDefinition[] = [];
  private server: net.Server | null = null;
  private sockets = new Set<net.Socket>();
  public isRunning = false;
  public serverMode: ModbusServerTransportMode = 'Tcp';
  public port = 5020;
  public station = 1;
  public serialParams: SerialPortParameters = {
    portName: 'COM1',
    baudRate: 9600,
    dataBits: 8,
    parity: 'None',
    stopBits: 'One',
    dtrEnable: true,
    rtsEnable: true,
  };
  public statusText = '服务器已停止';
  public onRegistersMutated?: () => void;

  configureRegisters(defs: ServerRegisterDefinition[]): void {
    const validated = defs.map((d) => ({ ...d }));
    for (const def of validated) {
      ServerRegisterCodec.validate(def);
    }
    ServerRegisterCodec.validateNoOverlap(validated);

    const isSameSpec = (a: ServerRegisterDefinition, b: ServerRegisterDefinition) =>
      a.area === b.area &&
      a.address === b.address &&
      a.dataType === b.dataType &&
      a.byteOrder === b.byteOrder &&
      a.initialValue === b.initialValue;

    const previousDefs = this.definitions;

    // Clear previous definitions that were removed or whose spec changed
    for (const oldDef of previousDefs) {
      const stillSame = validated.find((n) => isSameSpec(n, oldDef));
      if (!stillSame) {
        this.applyDefinition(oldDef, true);
      }
    }

    this.definitions = validated.sort((a, b) =>
      a.area === b.area ? a.address - b.address : a.area.localeCompare(b.area)
    );

    // Apply initial value only for newly added or modified definitions so existing runtime values stay intact
    for (const def of this.definitions) {
      const existedUnchanged = previousDefs.find((oldDef) => isSameSpec(oldDef, def));
      if (!existedUnchanged) {
        this.applyDefinition(def, false);
      }
    }
  }

  resetToInitialValues(): void {
    for (const def of this.definitions) {
      this.applyDefinition(def, false);
    }
    this.onRegistersMutated?.();
  }

  getDefinitionsWithCurrentValues(): ServerRegisterDefinition[] {
    return this.definitions.map((def) => ({
      ...def,
      currentValue: this.getValue(def),
    }));
  }

  getValue(def: ServerRegisterDefinition): string {
    if (def.area === 'Coil') {
      return this.coils[def.address] ? 'true' : 'false';
    }
    if (def.area === 'DiscreteInput') {
      return this.discreteInputs[def.address] ? 'true' : 'false';
    }
    const source =
      def.area === 'HoldingRegister' ? this.holdingRegisters : this.inputRegisters;
    const width = ServerRegisterCodec.getWidth(def.dataType);
    const slice: number[] = [];
    for (let i = 0; i < width; i++) {
      slice.push(source[def.address + i]);
    }
    return ServerRegisterCodec.decodeRegisters(slice, def.dataType, def.byteOrder);
  }

  setRuntimeValue(
    area: ServerRegisterArea,
    address: number,
    value: string
  ): void {
    const def = this.definitions.find(
      (d) => d.area === area && d.address === address
    );
    if (!def) {
      throw new Error(`未找到 ${area} 地址 ${address} 的数据定义`);
    }
    if (area === 'Coil' || area === 'DiscreteInput') {
      const bit = ServerRegisterCodec.encodeBit(value) ? 1 : 0;
      const target = area === 'Coil' ? this.coils : this.discreteInputs;
      target[address] = bit;
    } else {
      const regs = ServerRegisterCodec.encodeRegisters(
        value,
        def.dataType,
        def.byteOrder
      );
      const target =
        area === 'HoldingRegister' ? this.holdingRegisters : this.inputRegisters;
      for (let i = 0; i < regs.length; i++) {
        target[address + i] = regs[i];
      }
    }
    this.onRegistersMutated?.();
  }

  private applyDefinition(def: ServerRegisterDefinition, clear: boolean): void {
    const width = ServerRegisterCodec.getWidth(def.dataType);
    if (def.area === 'Coil' || def.area === 'DiscreteInput') {
      const target = def.area === 'Coil' ? this.coils : this.discreteInputs;
      const val = !clear && ServerRegisterCodec.encodeBit(def.initialValue) ? 1 : 0;
      for (let i = 0; i < width; i++) {
        target[def.address + i] = val;
      }
      return;
    }

    const target =
      def.area === 'HoldingRegister' ? this.holdingRegisters : this.inputRegisters;
    if (clear) {
      for (let i = 0; i < width; i++) {
        target[def.address + i] = 0;
      }
      return;
    }
    const encoded = ServerRegisterCodec.encodeRegisters(
      def.initialValue,
      def.dataType,
      def.byteOrder
    );
    for (let i = 0; i < encoded.length; i++) {
      target[def.address + i] = encoded[i];
    }
  }

  isTcpActive(): boolean {
    return (
      this.isRunning &&
      (this.serverMode === 'Tcp' || this.serverMode === 'TcpAndSerial')
    );
  }

  isSerialActive(): boolean {
    return (
      this.isRunning &&
      (this.serverMode === 'SerialRtu' ||
        this.serverMode === 'SerialAscii' ||
        this.serverMode === 'TcpAndSerial')
    );
  }

  buildRunningStatusText(): string {
    const serialSummary = `${this.serialParams.portName} (${this.serialParams.baudRate},${this.serialParams.dataBits},${this.serialParams.parity},${this.serialParams.stopBits})`;
    const count = this.definitions.length;
    switch (this.serverMode) {
      case 'SerialRtu':
        return `运行中：串口 RTU ${serialSummary}，站号 ${this.station}，数据定义 ${count} 项`;
      case 'SerialAscii':
        return `运行中：串口 ASCII ${serialSummary}，站号 ${this.station}，数据定义 ${count} 项`;
      case 'TcpAndSerial':
        return `运行中：TCP ${this.port} + 串口 ${serialSummary}，站号 ${this.station}，数据定义 ${count} 项`;
      case 'Tcp':
      default:
        return `运行中：TCP 端口 ${this.port}，站号 ${this.station}，数据定义 ${count} 项`;
    }
  }

  async start(
    port: number,
    station = 1,
    mode: ModbusServerTransportMode = 'Tcp',
    serial?: SerialPortParameters
  ): Promise<void> {
    if (!Number.isInteger(station) || station < 1 || station > 247) {
      throw new Error('站号必须在 1 至 247 之间');
    }
    const needsTcp = mode === 'Tcp' || mode === 'TcpAndSerial';
    const needsSerial =
      mode === 'SerialRtu' || mode === 'SerialAscii' || mode === 'TcpAndSerial';

    if (needsTcp && (!Number.isInteger(port) || port < 1 || port > 65535)) {
      throw new Error('服务器端口必须在 1 至 65535 之间');
    }
    if (needsSerial) {
      const targetSerial = serial ?? this.serialParams;
      if (!targetSerial.portName?.trim()) {
        throw new Error('请选择服务器串口号');
      }
      if (!Number.isInteger(targetSerial.baudRate) || targetSerial.baudRate <= 0) {
        throw new Error('无效的服务器串口波特率');
      }
    }

    await this.stop();

    this.serverMode = mode;
    this.station = station;
    if (needsTcp) {
      this.port = port;
    }
    if (serial) {
      this.serialParams = { ...serial };
    }

    if (needsTcp) {
      await new Promise<void>((resolve, reject) => {
        const srv = net.createServer((socket) => this.handleClient(socket));
        srv.once('error', (err) => {
          reject(err);
        });
        srv.listen(port, '0.0.0.0', () => {
          this.server = srv;
          resolve();
        });
      });
    }

    this.isRunning = true;
    this.statusText = this.buildRunningStatusText();
  }

  handleSerialRequest(
    clientSerial: SerialPortParameters,
    bytes: Uint8Array
  ): Uint8Array | null {
    if (!this.isSerialActive()) {
      return null;
    }

    const srv = this.serialParams;
    if (
      clientSerial.portName !== srv.portName ||
      clientSerial.baudRate !== srv.baudRate ||
      clientSerial.dataBits !== srv.dataBits ||
      clientSerial.parity !== srv.parity ||
      clientSerial.stopBits !== srv.stopBits
    ) {
      return null;
    }

    const allowRtu =
      this.serverMode === 'SerialRtu' || this.serverMode === 'TcpAndSerial';
    const allowAscii =
      this.serverMode === 'SerialAscii' || this.serverMode === 'TcpAndSerial';

    if (allowRtu && bytes.length >= 4 && Crc16.verify(bytes)) {
      const unitId = bytes[0];
      const pdu = bytes.subarray(1, bytes.length - 2);
      const responsePdu = this.processRequest(unitId, pdu);
      const rtuBody = new Uint8Array(1 + responsePdu.length);
      rtuBody[0] = unitId;
      rtuBody.set(responsePdu, 1);
      const crc = Crc16.compute(rtuBody);
      const frame = new Uint8Array(rtuBody.length + 2);
      frame.set(rtuBody, 0);
      frame[frame.length - 2] = crc & 0xff;
      frame[frame.length - 1] = (crc >>> 8) & 0xff;
      return frame;
    }

    if (
      allowAscii &&
      bytes.length >= 7 &&
      bytes[0] === 0x3a &&
      bytes[bytes.length - 2] === 0x0d &&
      bytes[bytes.length - 1] === 0x0a
    ) {
      try {
        let hexStr = '';
        for (let i = 1; i < bytes.length - 2; i++) {
          hexStr += String.fromCharCode(bytes[i]);
        }
        const raw = ByteUtils.hexToBytes(hexStr);
        if (LrcUtils.verify(raw) && raw.length >= 3) {
          const unitId = raw[0];
          const pdu = raw.subarray(1, raw.length - 1);
          const responsePdu = this.processRequest(unitId, pdu);
          const respBody = new Uint8Array(1 + responsePdu.length);
          respBody[0] = unitId;
          respBody.set(responsePdu, 1);
          const lrc = LrcUtils.compute(respBody);
          const lrcHex = lrc.toString(16).toUpperCase().padStart(2, '0');
          const out = `:${ByteUtils.bytesToHex(respBody, '')}${lrcHex}\r\n`;
          return Uint8Array.from(out, (c) => c.charCodeAt(0));
        }
      } catch {
        return null;
      }
    }

    return null;
  }

  async stop(): Promise<void> {
    for (const socket of this.sockets) {
      socket.destroy();
    }
    this.sockets.clear();

    if (this.server) {
      const srv = this.server;
      this.server = null;
      await new Promise<void>((resolve) => {
        srv.close(() => resolve());
      });
    }
    this.isRunning = false;
    this.statusText = '服务器已停止';
  }

  private handleClient(socket: net.Socket): void {
    this.sockets.add(socket);
    socket.setNoDelay(true);
    let buffer = Buffer.alloc(0);

    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 7) {
        const protocolId = buffer.readUInt16BE(2);
        if (protocolId !== 0) {
          socket.destroy();
          return;
        }
        const length = buffer.readUInt16BE(4);
        if (length < 2 || length > 254) {
          socket.destroy();
          return;
        }
        const totalLength = 6 + length;
        if (buffer.length < totalLength) {
          break;
        }

        const frame = buffer.subarray(0, totalLength);
        buffer = buffer.subarray(totalLength);

        const unitId = frame[6];
        const pdu = new Uint8Array(frame.subarray(7));
        const responsePdu = this.processRequest(unitId, pdu);
        const response = Buffer.alloc(7 + responsePdu.length);
        frame.copy(response, 0, 0, 4);
        response.writeUInt16BE(responsePdu.length + 1, 4);
        response[6] = unitId;
        Buffer.from(responsePdu).copy(response, 7);
        socket.write(response);
      }
    });

    socket.on('close', () => {
      this.sockets.delete(socket);
    });

    socket.on('error', () => {
      this.sockets.delete(socket);
    });
  }

  processRequest(unitId: number, request: Uint8Array): Uint8Array {
    if (request.length < 1) {
      return new Uint8Array([0x80, 0x03]);
    }
    const fn = request[0];
    if (unitId !== this.station) {
      return new Uint8Array([fn | 0x80, 0x0b]);
    }

    try {
      switch (fn) {
        case 1:
          return this.readBits(fn, request, this.coils);
        case 2:
          return this.readBits(fn, request, this.discreteInputs);
        case 3:
          return this.readRegisters(fn, request, this.holdingRegisters);
        case 4:
          return this.readRegisters(fn, request, this.inputRegisters);
        case 5:
          return this.writeSingleCoil(request);
        case 6:
          return this.writeSingleRegister(request);
        case 15:
          return this.writeMultipleCoils(fn, request);
        case 16:
          return this.writeMultipleRegisters(fn, request);
        default:
          return new Uint8Array([fn | 0x80, 0x01]);
      }
    } catch (err) {
      const code = err instanceof ModbusErrorCode ? err.code : 0x02;
      return new Uint8Array([fn | 0x80, code]);
    }
  }

  private readBits(
    fn: number,
    request: Uint8Array,
    source: Uint8Array
  ): Uint8Array {
    if (request.length !== 5) throw new ModbusErrorCode(0x03);
    const address = (request[1] << 8) | request[2];
    const quantity = (request[3] << 8) | request[4];
    if (quantity === 0 || quantity > 2000 || address + quantity > source.length) {
      throw new ModbusErrorCode(0x02);
    }
    const byteCount = Math.ceil(quantity / 8);
    const response = new Uint8Array(2 + byteCount);
    response[0] = fn;
    response[1] = byteCount;
    for (let i = 0; i < quantity; i++) {
      if (source[address + i] !== 0) {
        response[2 + Math.floor(i / 8)] |= 1 << (i % 8);
      }
    }
    return response;
  }

  private readRegisters(
    fn: number,
    request: Uint8Array,
    source: Uint16Array
  ): Uint8Array {
    if (request.length !== 5) throw new ModbusErrorCode(0x03);
    const address = (request[1] << 8) | request[2];
    const quantity = (request[3] << 8) | request[4];
    if (quantity === 0 || quantity > 125 || address + quantity > source.length) {
      throw new ModbusErrorCode(0x02);
    }
    const response = new Uint8Array(2 + quantity * 2);
    response[0] = fn;
    response[1] = quantity * 2;
    for (let i = 0; i < quantity; i++) {
      const val = source[address + i];
      response[2 + i * 2] = (val >>> 8) & 0xff;
      response[3 + i * 2] = val & 0xff;
    }
    return response;
  }

  private writeSingleCoil(request: Uint8Array): Uint8Array {
    if (request.length !== 5) throw new ModbusErrorCode(0x03);
    const address = (request[1] << 8) | request[2];
    const value = (request[3] << 8) | request[4];
    if (value !== 0x0000 && value !== 0xff00) throw new ModbusErrorCode(0x03);
    this.ensureWritable('Coil', address);
    this.coils[address] = value === 0xff00 ? 1 : 0;
    this.onRegistersMutated?.();
    return new Uint8Array(request);
  }

  private writeSingleRegister(request: Uint8Array): Uint8Array {
    if (request.length !== 5) throw new ModbusErrorCode(0x03);
    const address = (request[1] << 8) | request[2];
    const value = (request[3] << 8) | request[4];
    this.ensureWritable('HoldingRegister', address);
    this.holdingRegisters[address] = value;
    this.onRegistersMutated?.();
    return new Uint8Array(request);
  }

  private writeMultipleCoils(fn: number, request: Uint8Array): Uint8Array {
    if (request.length < 6) throw new ModbusErrorCode(0x03);
    const address = (request[1] << 8) | request[2];
    const quantity = (request[3] << 8) | request[4];
    const byteCount = request[5];
    if (
      quantity === 0 ||
      quantity > 1968 ||
      byteCount !== Math.ceil(quantity / 8) ||
      request.length !== 6 + byteCount ||
      address + quantity > this.coils.length
    ) {
      throw new ModbusErrorCode(0x03);
    }
    for (let i = 0; i < quantity; i++) {
      this.ensureWritable('Coil', address + i);
    }
    for (let i = 0; i < quantity; i++) {
      this.coils[address + i] =
        (request[6 + Math.floor(i / 8)] & (1 << (i % 8))) !== 0 ? 1 : 0;
    }
    this.onRegistersMutated?.();
    return new Uint8Array([fn, request[1], request[2], request[3], request[4]]);
  }

  private writeMultipleRegisters(fn: number, request: Uint8Array): Uint8Array {
    if (request.length < 6) throw new ModbusErrorCode(0x03);
    const address = (request[1] << 8) | request[2];
    const quantity = (request[3] << 8) | request[4];
    const byteCount = request[5];
    if (
      quantity === 0 ||
      quantity > 123 ||
      byteCount !== quantity * 2 ||
      request.length !== 6 + byteCount ||
      address + quantity > this.holdingRegisters.length
    ) {
      throw new ModbusErrorCode(0x03);
    }
    for (let i = 0; i < quantity; i++) {
      this.ensureWritable('HoldingRegister', address + i);
    }
    for (let i = 0; i < quantity; i++) {
      this.holdingRegisters[address + i] =
        (request[6 + i * 2] << 8) | request[7 + i * 2];
    }
    this.onRegistersMutated?.();
    return new Uint8Array([fn, request[1], request[2], request[3], request[4]]);
  }

  private ensureWritable(area: ServerRegisterArea, address: number): void {
    const found = this.definitions.find(
      (def) =>
        def.area === area &&
        address >= def.address &&
        address < def.address + ServerRegisterCodec.getWidth(def.dataType)
    );
    if (!found) {
      throw new ModbusErrorCode(0x02);
    }
  }
}

class ModbusErrorCode extends Error {
  constructor(public readonly code: number) {
    super(`Modbus exception 0x${code.toString(16)}`);
  }
}

class CommunicationManager {
  private state: ChannelState = 'Disconnected';
  private channelType: CommunicationType | null = null;
  private channelInfo = '无活动连接';
  private deviceInfo = '就绪';
  private stats: ChannelStatistics = {
    bytesSent: 0,
    bytesReceived: 0,
    packetsSent: 0,
    packetsReceived: 0,
    errorsCount: 0,
  };

  private tcpSocket: net.Socket | null = null;
  private tcpParams: TcpClientParameters | null = null;
  private reconnectTimer: NodeJS.Timeout | null = null;
  private manualClosing = false;

  private udpSocket: dgram.Socket | null = null;
  private udpParams: UdpParameters | null = null;

  private serialParams: SerialPortParameters | null = null;

  private rawTcpServer: net.Server | null = null;
  private rawTcpParams: TcpServerParameters | null = null;
  private rawTcpClients = new Map<
    string,
    { socket: net.Socket; session: TcpServerClientSession }
  >();
  private nextSessionCounter = 1;

  private pendingExchange: {
    resolve: (data: Uint8Array) => void;
    reject: (err: Error) => void;
    timer: NodeJS.Timeout;
  } | null = null;
  private exchangeQueue: Promise<void> = Promise.resolve();

  constructor(
    private readonly modbusServer: ModbusTcpServerService,
    private readonly broadcast: (event: string, payload: unknown) => void
  ) {}

  getSnapshot(): CommunicationStatusSnapshot {
    const stateTextMap: Record<ChannelState, string> = {
      Disconnected: '未连接',
      Connecting: '正在连接...',
      Connected: '已连接',
      Reconnecting: '正在重连...',
      Faulted: '连接异常',
    };
    return {
      isConnected: this.state === 'Connected',
      state: this.state,
      stateText: stateTextMap[this.state],
      communicationType: this.channelInfo,
      channelType: this.channelType,
      statistics: { ...this.stats },
      deviceInfo: this.deviceInfo,
      tcpServerClients: Array.from(this.rawTcpClients.values()).map((c) => ({
        ...c.session,
      })),
      isServerRunning: this.modbusServer.isRunning,
      serverMode: this.modbusServer.serverMode,
      serverPort: this.modbusServer.port,
      serverStation: this.modbusServer.station,
      serverSerial: { ...this.modbusServer.serialParams },
      serverStatusText: this.modbusServer.statusText,
      serverRegisters: this.modbusServer.getDefinitionsWithCurrentValues(),
    };
  }

  private emitState(): void {
    this.broadcast('state', this.getSnapshot());
  }

  private emitTraffic(
    direction: 'TX' | 'RX',
    bytes: Uint8Array,
    sessionId?: string,
    sessionLabel?: string
  ): void {
    const evt: TrafficEvent = {
      direction,
      bytes: Array.from(bytes),
      hex: ByteUtils.bytesToHex(bytes),
      timestamp: new Date().toISOString(),
      epochMs: Date.now(),
      channelType: this.channelType,
      sessionId,
      sessionLabel,
    };
    this.broadcast('traffic', evt);
  }

  private emitDataReceived(
    bytes: Uint8Array,
    sessionId?: string,
    sessionLabel?: string
  ): void {
    this.stats.bytesReceived += bytes.length;
    this.stats.packetsReceived += 1;

    if (this.pendingExchange) {
      const pending = this.pendingExchange;
      this.pendingExchange = null;
      clearTimeout(pending.timer);
      pending.resolve(bytes);
    }

    this.broadcast('dataReceived', {
      hex: ByteUtils.bytesToHex(bytes),
      bytes: Array.from(bytes),
      timestamp: new Date().toISOString(),
      sessionId,
      sessionLabel,
      statistics: { ...this.stats },
    });

    this.emitTraffic('RX', bytes, sessionId, sessionLabel);
  }

  private emitError(message: string): void {
    this.stats.errorsCount += 1;
    this.deviceInfo = `错误: ${message}`;
    this.broadcast('error', {
      message,
      statistics: { ...this.stats },
    });
    this.emitState();
  }

  async connectSerial(params: SerialPortParameters): Promise<void> {
    await this.disconnect(false);
    this.manualClosing = false;
    this.channelType = 'SerialPort';
    this.serialParams = params;
    this.channelInfo = `${params.portName} (${params.baudRate},${params.dataBits},${params.parity},${params.stopBits}) [仿真]`;
    this.state = 'Connected';
    this.deviceInfo = `虚拟串口已就绪 (${params.portName})`;
    this.emitState();
  }

  async connectTcp(params: TcpClientParameters): Promise<void> {
    await this.disconnect(false);
    this.manualClosing = false;
    this.channelType = 'TcpClient';
    this.tcpParams = params;
    this.channelInfo = `TCP Client (${params.host}:${params.port})`;
    this.state = 'Connecting';
    this.deviceInfo = `正在连接 ${params.host}:${params.port}...`;
    this.emitState();

    await this.openTcpSocket(params);
  }

  private openTcpSocket(params: TcpClientParameters): Promise<void> {
    return new Promise((resolve, reject) => {
      const socket = new net.Socket();
      socket.setNoDelay(true);
      let settled = false;

      const timeoutMs = Math.max(200, params.connectTimeoutMs || 3000);
      const connectTimer = setTimeout(() => {
        if (!settled) {
          settled = true;
          socket.destroy();
          const err = new Error(`TCP 连接超时 (${timeoutMs}ms)`);
          this.state = 'Faulted';
          this.emitError(err.message);
          if (params.autoReconnect && !this.manualClosing) {
            this.scheduleReconnect();
          }
          reject(err);
        }
      }, timeoutMs);

      socket.connect(params.port, params.host, () => {
        clearTimeout(connectTimer);
        if (settled) return;
        settled = true;
        this.tcpSocket = socket;
        this.state = 'Connected';
        this.deviceInfo = `已连接 ${params.host}:${params.port}`;
        this.emitState();
        resolve();
      });

      socket.on('data', (chunk) => {
        this.emitDataReceived(new Uint8Array(chunk));
      });

      socket.on('error', (err) => {
        clearTimeout(connectTimer);
        if (!settled) {
          settled = true;
          this.state = 'Faulted';
          this.emitError(err.message);
          if (params.autoReconnect && !this.manualClosing) {
            this.scheduleReconnect();
          }
          reject(err);
        } else if (!this.manualClosing) {
          this.state = 'Faulted';
          this.emitError(err.message);
          if (params.autoReconnect) {
            this.scheduleReconnect();
          }
        }
      });

      socket.on('close', () => {
        clearTimeout(connectTimer);
        if (this.tcpSocket === socket) {
          this.tcpSocket = null;
        }
        if (settled && !this.manualClosing && this.state === 'Connected') {
          this.state = 'Faulted';
          this.emitError('远程主机已关闭连接 (EOF)');
          if (params.autoReconnect) {
            this.scheduleReconnect();
          }
        }
      });
    });
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer || this.manualClosing || !this.tcpParams) return;
    this.state = 'Reconnecting';
    this.deviceInfo = '正在等待自动重连...';
    this.emitState();

    const interval = this.tcpParams.reconnectIntervalMs || 3000;
    this.reconnectTimer = setTimeout(async () => {
      this.reconnectTimer = null;
      if (this.manualClosing || !this.tcpParams) return;
      try {
        this.state = 'Connecting';
        this.emitState();
        await this.openTcpSocket(this.tcpParams);
      } catch {
        // openTcpSocket handles scheduling next retry
      }
    }, interval);
  }

  async connectUdp(params: UdpParameters): Promise<void> {
    await this.disconnect(false);
    this.manualClosing = false;
    this.channelType = 'Udp';
    this.udpParams = params;
    this.channelInfo = `UDP (${params.remoteHost}:${params.remotePort})`;
    this.state = 'Connecting';
    this.emitState();

    await new Promise<void>((resolve, reject) => {
      const socket = dgram.createSocket('udp4');
      socket.once('error', (err) => {
        socket.close();
        this.state = 'Faulted';
        this.emitError(err.message);
        reject(err);
      });

      socket.on('message', (msg) => {
        this.emitDataReceived(new Uint8Array(msg));
      });

      const localPort = params.localPort > 0 ? params.localPort : 0;
      socket.bind(localPort, '0.0.0.0', () => {
        socket.removeAllListeners('error');
        socket.on('error', (err) => {
          if (!this.manualClosing) {
            this.state = 'Faulted';
            this.emitError(err.message);
          }
        });
        this.udpSocket = socket;
        this.state = 'Connected';
        this.deviceInfo = `UDP 就绪 -> ${params.remoteHost}:${params.remotePort}`;
        this.emitState();
        resolve();
      });
    });
  }

  async connectTcpServer(params: TcpServerParameters): Promise<void> {
    if (
      !Number.isInteger(params.listenPort) ||
      params.listenPort < 1 ||
      params.listenPort > 65535
    ) {
      throw new Error('监听端口必须在 1 至 65535 之间');
    }

    await this.disconnect(false);
    this.manualClosing = false;
    this.channelType = 'TcpServer';
    this.rawTcpParams = params;
    this.channelInfo = `TCP Server (监听 :${params.listenPort})`;
    this.state = 'Connecting';
    this.emitState();

    await new Promise<void>((resolve, reject) => {
      const srv = net.createServer((socket) => {
        socket.setNoDelay(true);
        const sessionId = `S${this.nextSessionCounter++}`;
        const remoteAddress = socket.remoteAddress || '127.0.0.1';
        const remotePort = socket.remotePort || 0;
        const sessionLabel = `${sessionId} · ${remoteAddress}:${remotePort}`;

        const session: TcpServerClientSession = {
          sessionId,
          remoteAddress,
          remotePort,
          connectedAt: new Date().toISOString(),
          bytesSent: 0,
          bytesReceived: 0,
        };
        this.rawTcpClients.set(sessionId, { socket, session });
        this.deviceInfo = `TCP Server 监听中 (${this.rawTcpClients.size} 个客户端在线)`;
        this.emitState();

        socket.on('data', (chunk) => {
          const bytes = new Uint8Array(chunk);
          session.bytesReceived += bytes.length;
          this.emitDataReceived(bytes, sessionId, sessionLabel);
          this.emitState();
        });

        socket.on('close', () => {
          this.rawTcpClients.delete(sessionId);
          if (this.channelType === 'TcpServer' && this.state === 'Connected') {
            this.deviceInfo = `TCP Server 监听中 (${this.rawTcpClients.size} 个客户端在线)`;
            this.emitState();
          }
        });

        socket.on('error', () => {
          this.rawTcpClients.delete(sessionId);
        });
      });

      srv.once('error', (err) => {
        this.state = 'Faulted';
        this.emitError(err.message);
        reject(err);
      });

      srv.listen(params.listenPort, '0.0.0.0', () => {
        srv.removeAllListeners('error');
        srv.on('error', (err) => {
          if (!this.manualClosing) {
            this.state = 'Faulted';
            this.emitError(err.message);
          }
        });
        this.rawTcpServer = srv;
        this.state = 'Connected';
        this.deviceInfo = `TCP Server 监听端口 ${params.listenPort} (0 个客户端在线)`;
        this.emitState();
        resolve();
      });
    });
  }

  disconnectTcpServerClient(sessionId: string): void {
    const entry = this.rawTcpClients.get(sessionId);
    if (!entry) return;
    entry.socket.destroy();
    this.rawTcpClients.delete(sessionId);
    if (this.channelType === 'TcpServer' && this.state === 'Connected') {
      this.deviceInfo = `TCP Server 监听中 (${this.rawTcpClients.size} 个客户端在线)`;
      this.emitState();
    }
  }

  async disconnect(emitUpdate = true): Promise<void> {
    this.manualClosing = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    if (this.pendingExchange) {
      const pending = this.pendingExchange;
      this.pendingExchange = null;
      clearTimeout(pending.timer);
      pending.reject(new Error('连接已断开'));
    }

    if (this.tcpSocket) {
      this.tcpSocket.destroy();
      this.tcpSocket = null;
    }
    this.tcpParams = null;

    if (this.udpSocket) {
      await new Promise<void>((resolve) => {
        this.udpSocket?.close(() => resolve());
      });
      this.udpSocket = null;
    }
    this.udpParams = null;
    this.serialParams = null;

    for (const { socket } of this.rawTcpClients.values()) {
      socket.destroy();
    }
    this.rawTcpClients.clear();
    if (this.rawTcpServer) {
      const srv = this.rawTcpServer;
      this.rawTcpServer = null;
      await new Promise<void>((resolve) => {
        srv.close(() => resolve());
      });
    }
    this.rawTcpParams = null;

    this.state = 'Disconnected';
    this.channelType = null;
    this.channelInfo = '无活动连接';
    this.deviceInfo = '就绪';
    if (emitUpdate) {
      this.emitState();
    }
  }

  async send(bytes: Uint8Array, targetSessionId?: string): Promise<number> {
    if (this.state !== 'Connected') {
      throw new Error('通讯链路未就绪或已断开，无法发送数据');
    }

    if (this.channelType === 'TcpServer') {
      if (!targetSessionId) {
        throw new Error('服务端模式下请先选择目标客户端后再发送数据');
      }
      const entry = this.rawTcpClients.get(targetSessionId);
      if (!entry) {
        throw new Error(`目标客户端 ${targetSessionId} 不存在或已断开`);
      }
      await new Promise<void>((resolve, reject) => {
        entry.socket.write(Buffer.from(bytes), (err) => {
          if (err) reject(err);
          else resolve();
        });
      });
      entry.session.bytesSent += bytes.length;
      this.stats.bytesSent += bytes.length;
      this.stats.packetsSent += 1;
      const label = `${entry.session.sessionId} · ${entry.session.remoteAddress}:${entry.session.remotePort}`;
      this.emitTraffic('TX', bytes, entry.session.sessionId, label);
      this.emitState();
      return bytes.length;
    }

    if (this.channelType === 'TcpClient' && this.tcpSocket) {
      await new Promise<void>((resolve, reject) => {
        this.tcpSocket!.write(Buffer.from(bytes), (err) => {
          if (err) reject(err);
          else resolve();
        });
      });
      this.stats.bytesSent += bytes.length;
      this.stats.packetsSent += 1;
      this.emitTraffic('TX', bytes);
      this.emitState();
      return bytes.length;
    }

    if (this.channelType === 'Udp' && this.udpSocket && this.udpParams) {
      await new Promise<void>((resolve, reject) => {
        this.udpSocket!.send(
          Buffer.from(bytes),
          this.udpParams!.remotePort,
          this.udpParams!.remoteHost,
          (err) => {
            if (err) reject(err);
            else resolve();
          }
        );
      });
      this.stats.bytesSent += bytes.length;
      this.stats.packetsSent += 1;
      this.emitTraffic('TX', bytes);
      this.emitState();
      return bytes.length;
    }

    if (this.channelType === 'SerialPort' && this.serialParams) {
      const activeSerial = this.serialParams;
      this.stats.bytesSent += bytes.length;
      this.stats.packetsSent += 1;
      this.emitTraffic('TX', bytes);
      this.emitState();

      setTimeout(() => {
        if (this.state !== 'Connected' || this.channelType !== 'SerialPort') return;
        const reply = this.modbusServer.handleSerialRequest(activeSerial, bytes);
        if (reply) {
          this.emitDataReceived(reply);
          return;
        }
        const looksLikeModbusRtu = bytes.length >= 4 && Crc16.verify(bytes);
        const looksLikeModbusAscii =
          bytes.length >= 7 &&
          bytes[0] === 0x3a &&
          bytes[bytes.length - 2] === 0x0d &&
          bytes[bytes.length - 1] === 0x0a;
        if (!looksLikeModbusRtu && !looksLikeModbusAscii) {
          // Echo back non-Modbus raw bytes in virtual serial mode for raw debugging visibility
          this.emitDataReceived(bytes);
        }
      }, 20);
      return bytes.length;
    }

    throw new Error('活动通讯通道不可用');
  }

  exchange(request: Uint8Array, timeoutMs = 3000): Promise<Uint8Array> {
    const run = async () => {
      if (this.state !== 'Connected') {
        throw new Error('通讯链路未就绪或已断开');
      }

      const responsePromise = new Promise<Uint8Array>((resolve, reject) => {
        const timer = setTimeout(() => {
          if (this.pendingExchange) {
            this.pendingExchange = null;
          }
          reject(new Error(`等待响应超时 (${timeoutMs}ms)`));
        }, Math.max(200, timeoutMs));

        this.pendingExchange = { resolve, reject, timer };
      });

      try {
        await this.send(request);
      } catch (err) {
        if (this.pendingExchange) {
          clearTimeout(this.pendingExchange.timer);
          this.pendingExchange = null;
        }
        throw err;
      }

      return await responsePromise;
    };

    const next = this.exchangeQueue.then(run, run);
    this.exchangeQueue = next.then(
      () => undefined,
      () => undefined
    );
    return next;
  }
}

async function main() {
  const app = express();
  app.use(express.json({ limit: '2mb' }));

  const server = http.createServer(app);
  const wss = new WebSocketServer({ server, path: '/ws' });

  const broadcast = (event: string, payload: unknown) => {
    const msg = JSON.stringify({ event, payload });
    for (const client of wss.clients) {
      if (client.readyState === WebSocket.OPEN) {
        client.send(msg);
      }
    }
  };

  const modbusServer = new ModbusTcpServerService();
  // Seed sensible default server registers matching industrial testing
  modbusServer.configureRegisters([
    {
      area: 'HoldingRegister',
      address: 0,
      dataType: 'UInt16',
      byteOrder: 'BigEndian',
      initialValue: '1234',
      note: '示例保持寄存器 #0',
    },
    {
      area: 'HoldingRegister',
      address: 1,
      dataType: 'Float',
      byteOrder: 'BigEndian',
      initialValue: '25.5',
      note: '温度传感器 (Float)',
    },
    {
      area: 'InputRegister',
      address: 0,
      dataType: 'Int16',
      byteOrder: 'BigEndian',
      initialValue: '500',
      note: '只读输入寄存器',
    },
    {
      area: 'Coil',
      address: 0,
      dataType: 'Bit',
      byteOrder: 'BigEndian',
      initialValue: 'true',
      note: '运行指示线圈',
    },
  ]);

  const commManager = new CommunicationManager(modbusServer, broadcast);
  modbusServer.onRegistersMutated = () => {
    broadcast('state', commManager.getSnapshot());
  };

  let deviceProfiles: DeviceProfile[] = [
    {
      id: 'default-local-tcp',
      name: '本地 Modbus TCP 仿真从站',
      protocol: 'Modbus TCP',
      endpoint: '127.0.0.1:5020',
      stationId: 1,
    },
    {
      id: 'default-serial-rtu',
      name: '虚拟串口 RTU 设备',
      protocol: 'Modbus RTU',
      endpoint: 'COM1 (9600,8,None,One)',
      stationId: 1,
    },
  ];

  let appSettings: AppSettings = {
    theme: 'light',
    defaultTimeoutMs: 1000,
    defaultRetryTimes: 2,
  };

  wss.on('connection', (ws) => {
    ws.send(
      JSON.stringify({
        event: 'state',
        payload: commManager.getSnapshot(),
      })
    );
  });

  // ====== API Routes ======
  app.get('/api/state', (_req, res) => {
    res.json({
      status: commManager.getSnapshot(),
      profiles: deviceProfiles,
      settings: appSettings,
    });
  });

  app.post('/api/comm/connect', async (req, res) => {
    try {
      const { type, serial, tcp, udp, tcpServer } = req.body as {
        type: CommunicationType;
        serial?: SerialPortParameters;
        tcp?: TcpClientParameters;
        udp?: UdpParameters;
        tcpServer?: TcpServerParameters;
      };
      if (type === 'SerialPort' && serial) {
        await commManager.connectSerial(serial);
      } else if (type === 'TcpClient' && tcp) {
        await commManager.connectTcp(tcp);
      } else if (type === 'Udp' && udp) {
        await commManager.connectUdp(udp);
      } else if (type === 'TcpServer' && tcpServer) {
        await commManager.connectTcpServer(tcpServer);
      } else {
        res.status(400).json({ error: '无效的通讯连接参数' });
        return;
      }
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '连接失败';
      res.status(400).json({ error: message, status: commManager.getSnapshot() });
    }
  });

  app.post('/api/comm/disconnect', async (_req, res) => {
    await commManager.disconnect(true);
    res.json({ ok: true, status: commManager.getSnapshot() });
  });

  app.post('/api/comm/server-clients/:sessionId/disconnect', (req, res) => {
    commManager.disconnectTcpServerClient(req.params.sessionId);
    res.json({ ok: true, status: commManager.getSnapshot() });
  });

  app.post('/api/comm/send', async (req, res) => {
    try {
      const { bytes, sessionId } = req.body as {
        bytes: number[];
        sessionId?: string;
      };
      if (!Array.isArray(bytes) || bytes.length === 0) {
        res.status(400).json({ error: '发送数据不能为空' });
        return;
      }
      const sent = await commManager.send(new Uint8Array(bytes), sessionId);
      res.json({ ok: true, sent, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '发送失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/state/restore', (req, res) => {
    try {
      const { profiles, settings, serverConfig } = req.body as {
        profiles?: DeviceProfile[];
        settings?: AppSettings;
        serverConfig?: ModbusServerConfiguration;
      };
      if (Array.isArray(profiles)) {
        deviceProfiles = profiles;
      }
      if (settings) {
        appSettings = {
          theme: settings.theme === 'dark' ? 'dark' : 'light',
          defaultTimeoutMs: Math.max(100, Number(settings.defaultTimeoutMs) || 1000),
          defaultRetryTimes: Math.max(
            0,
            Number.isFinite(Number(settings.defaultRetryTimes))
              ? Number(settings.defaultRetryTimes)
              : 2
          ),
        };
      }
      if (
        serverConfig &&
        serverConfig.version === 1 &&
        Array.isArray(serverConfig.registers)
      ) {
        modbusServer.port = serverConfig.port;
        modbusServer.station = serverConfig.station;
        if (serverConfig.serverMode) {
          modbusServer.serverMode = serverConfig.serverMode;
        }
        if (serverConfig.serverSerial) {
          modbusServer.serialParams = { ...serverConfig.serverSerial };
        }
        modbusServer.configureRegisters(serverConfig.registers);
      }
      broadcast('state', commManager.getSnapshot());
      res.json({
        ok: true,
        status: commManager.getSnapshot(),
        profiles: deviceProfiles,
        settings: appSettings,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : '状态恢复失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/modbus/exchange', async (req, res) => {
    try {
      const { requestHex, timeoutMs } = req.body as {
        requestHex: string;
        timeoutMs?: number;
      };
      const requestBytes = ByteUtils.hexToBytes(requestHex);
      if (requestBytes.length === 0) {
        res.status(400).json({ error: '请求报文不能为空' });
        return;
      }
      const responseBytes = await commManager.exchange(
        requestBytes,
        timeoutMs ?? appSettings.defaultTimeoutMs
      );
      res.json({
        ok: true,
        responseHex: ByteUtils.bytesToHex(responseBytes),
        responseBytes: Array.from(responseBytes),
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : '请求失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/start', async (req, res) => {
    try {
      const { port, station, mode, serial } = req.body as {
        port: number;
        station: number;
        mode?: ModbusServerTransportMode;
        serial?: SerialPortParameters;
      };
      await modbusServer.start(
        Number(port),
        Number(station),
        mode ?? modbusServer.serverMode,
        serial
      );
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '服务器启动失败';
      modbusServer.statusText = `启动失败：${message}`;
      broadcast('state', commManager.getSnapshot());
      res.status(400).json({ error: message, status: commManager.getSnapshot() });
    }
  });

  app.post('/api/server/stop', async (_req, res) => {
    await modbusServer.stop();
    broadcast('state', commManager.getSnapshot());
    res.json({ ok: true, status: commManager.getSnapshot() });
  });

  app.post('/api/server/registers', (req, res) => {
    try {
      const def = req.body as ServerRegisterDefinition;
      ServerRegisterCodec.validate(def);
      const current = modbusServer.getDefinitionsWithCurrentValues();
      const updated = [
        ...current
          .filter((item) => item.area !== def.area || item.address !== def.address)
          .map(({ currentValue: _cv, ...rest }) => rest),
        {
          area: def.area,
          address: Number(def.address),
          dataType: def.dataType,
          byteOrder: def.byteOrder,
          initialValue: def.initialValue,
          note: def.note ?? '',
        },
      ];
      ServerRegisterCodec.validateNoOverlap(updated);
      modbusServer.configureRegisters(updated);
      modbusServer.statusText = modbusServer.isRunning
        ? modbusServer.buildRunningStatusText()
        : `已配置 ${updated.length} 项数据定义`;
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '建立数据定义失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/registers/runtime', (req, res) => {
    try {
      const { area, address, value } = req.body as {
        area: ServerRegisterArea;
        address: number;
        value: string;
      };
      modbusServer.setRuntimeValue(area, Number(address), String(value));
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '写入当前值失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/registers/reset', (_req, res) => {
    try {
      modbusServer.resetToInitialValues();
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '重置初始值失败';
      res.status(400).json({ error: message });
    }
  });

  app.delete('/api/server/registers', (req, res) => {
    try {
      const area = req.query.area as ServerRegisterArea;
      const address = Number(req.query.address);
      const current = modbusServer.getDefinitionsWithCurrentValues();
      const updated = current
        .filter((item) => item.area !== area || item.address !== address)
        .map(({ currentValue: _cv, ...rest }) => rest);
      modbusServer.configureRegisters(updated);
      modbusServer.statusText = modbusServer.isRunning
        ? modbusServer.buildRunningStatusText()
        : `已配置 ${updated.length} 项数据定义`;
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '删除数据定义失败';
      res.status(400).json({ error: message });
    }
  });

  app.post('/api/server/import', (req, res) => {
    try {
      const config = req.body as ModbusServerConfiguration;
      if (!config || config.version !== 1) {
        throw new Error(`不支持的配置版本：${config?.version}`);
      }
      if (config.port < 1 || config.port > 65535) {
        throw new Error('服务器端口必须在 1 至 65535 之间');
      }
      if (config.station < 1 || config.station > 247) {
        throw new Error('站号必须在 1 至 247 之间');
      }
      if (!Array.isArray(config.registers)) {
        throw new Error('寄存器配置列表格式不正确');
      }
      modbusServer.port = config.port;
      modbusServer.station = config.station;
      if (config.serverMode) {
        modbusServer.serverMode = config.serverMode;
      }
      if (config.serverSerial) {
        modbusServer.serialParams = { ...config.serverSerial };
      }
      modbusServer.configureRegisters(config.registers);
      modbusServer.statusText = modbusServer.isRunning
        ? modbusServer.buildRunningStatusText()
        : `已加载 ${config.registers.length} 项数据定义`;
      broadcast('state', commManager.getSnapshot());
      res.json({ ok: true, status: commManager.getSnapshot() });
    } catch (err) {
      const message = err instanceof Error ? err.message : '配置加载失败';
      res.status(400).json({ error: message });
    }
  });

  app.get('/api/profiles', (_req, res) => {
    res.json({ profiles: deviceProfiles });
  });

  app.post('/api/profiles', (req, res) => {
    const profile = req.body as Omit<DeviceProfile, 'id'> & { id?: string };
    const created: DeviceProfile = {
      id: profile.id || `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
      name: profile.name || '新设备',
      protocol: profile.protocol || 'Modbus TCP',
      endpoint: profile.endpoint || '127.0.0.1:502',
      stationId: Number(profile.stationId) || 1,
    };
    deviceProfiles = [...deviceProfiles, created];
    res.json({ ok: true, profiles: deviceProfiles });
  });

  app.delete('/api/profiles/:id', (req, res) => {
    deviceProfiles = deviceProfiles.filter((p) => p.id !== req.params.id);
    res.json({ ok: true, profiles: deviceProfiles });
  });

  app.post('/api/settings', (req, res) => {
    const body = req.body as Partial<AppSettings>;
    appSettings = {
      theme: body.theme === 'dark' ? 'dark' : 'light',
      defaultTimeoutMs: Math.max(100, Number(body.defaultTimeoutMs) || 1000),
      defaultRetryTimes: Math.max(
        0,
        Number.isFinite(Number(body.defaultRetryTimes))
          ? Number(body.defaultRetryTimes)
          : 2
      ),
    };
    res.json({ ok: true, settings: appSettings });
  });

  if (process.env.NODE_ENV === 'production') {
    const distDir = path.resolve(__dirname, 'dist');
    app.use(express.static(distDir));
    app.get('*', (_req, res) => {
      res.sendFile(path.join(distDir, 'index.html'));
    });
  } else {
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: 'spa',
    });
    app.use(vite.middlewares);
  }

  server.listen(PORT, '0.0.0.0', () => {
    console.log(`Yuandl Modbus Assistant server listening on http://0.0.0.0:${PORT}`);
  });
}

main().catch((err) => {
  console.error('Failed to start server:', err);
  process.exit(1);
});
