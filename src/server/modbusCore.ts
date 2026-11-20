import net from 'node:net';
import dgram from 'node:dgram';
import type {
  ChannelState,
  ChannelStatistics,
  CommunicationStatusSnapshot,
  CommunicationType,
  ModbusServerTransportMode,
  ModbusTransport,
  SerialPortParameters,
  ServerRegisterArea,
  ServerRegisterDefinition,
  TcpClientParameters,
  TcpServerClientSession,
  TcpServerParameters,
  TrafficEvent,
  UdpParameters,
} from '../core/types.js';
import { ByteUtils } from '../core/utils/byteUtils.js';
import { Crc16 } from '../core/utils/crc16.js';
import { LrcUtils } from '../core/utils/lrcUtils.js';
import { ModbusFrame } from '../protocol/modbus/modbusFrame.js';
import { ServerRegisterCodec } from '../protocol/modbus/serverRegisterCodec.js';

export class ModbusErrorCode extends Error {
  constructor(public readonly code: number) {
    super(`Modbus exception 0x${code.toString(16)}`);
  }
}

export interface RequestMeta {
  transport: ModbusTransport;
  unitId: number;
  fn: number;
  transactionId?: number;
}

export function extractRequestMeta(
  request: Uint8Array,
  transportHint?: ModbusTransport
): RequestMeta | null {
  if (request.length === 0) return null;

  if (
    transportHint === 'Ascii' ||
    (!transportHint &&
      request.length >= 7 &&
      request[0] === 0x3a &&
      request[request.length - 2] === 0x0d &&
      request[request.length - 1] === 0x0a)
  ) {
    if (request.length < 7 || request[0] !== 0x3a) return null;
    try {
      let hex = '';
      for (let i = 1; i < request.length - 2; i++) {
        hex += String.fromCharCode(request[i]);
      }
      const raw = ByteUtils.hexToBytes(hex);
      if (raw.length < 3) return null;
      return {
        transport: 'Ascii',
        unitId: raw[0],
        fn: raw[1],
      };
    } catch {
      return null;
    }
  }

  const looksLikeTcp =
    transportHint === 'Tcp' ||
    (!transportHint &&
      request.length >= 8 &&
      ((request[2] << 8) | request[3]) === 0 &&
      ((request[4] << 8) | request[5]) === request.length - 6);

  if (looksLikeTcp) {
    if (request.length < 8) return null;
    return {
      transport: 'Tcp',
      transactionId: (request[0] << 8) | request[1],
      unitId: request[6],
      fn: request[7],
    };
  }

  if (request.length < 4) return null;
  return {
    transport: 'Rtu',
    unitId: request[0],
    fn: request[1],
  };
}

export function tryExtractMatchingFrame(
  buffer: number[],
  transport: ModbusTransport,
  expectedTxId?: number,
  expectedUnitId?: number,
  expectedFn?: number
): Uint8Array | null {
  if (transport === 'Tcp') {
    while (buffer.length >= 7) {
      const protocolId = (buffer[2] << 8) | buffer[3];
      const declaredLength = (buffer[4] << 8) | buffer[5];
      if (protocolId !== 0 || declaredLength < 2 || declaredLength > 260) {
        buffer.shift();
        continue;
      }

      const totalLength = 6 + declaredLength;
      if (buffer.length < totalLength) {
        return null;
      }

      const candidate = new Uint8Array(buffer.splice(0, totalLength));
      if (candidate.length < 8) continue;

      const respTxId = (candidate[0] << 8) | candidate[1];
      const respUnitId = candidate[6];
      const respFn = candidate[7];

      if (expectedTxId !== undefined && respTxId !== expectedTxId) {
        continue;
      }
      if (expectedUnitId !== undefined && respUnitId !== expectedUnitId) {
        continue;
      }
      if (
        expectedFn !== undefined &&
        respFn !== expectedFn &&
        respFn !== (expectedFn | 0x80)
      ) {
        continue;
      }

      return candidate;
    }
    return null;
  }

  if (transport === 'Ascii') {
    while (buffer.length > 0) {
      const start = buffer.indexOf(0x3a);
      if (start < 0) {
        buffer.length = 0;
        return null;
      }
      if (start > 0) {
        buffer.splice(0, start);
      }

      let crlfEnd = -1;
      for (let i = 1; i < buffer.length; i++) {
        if (buffer[i - 1] === 0x0d && buffer[i] === 0x0a) {
          crlfEnd = i;
          break;
        }
      }
      if (crlfEnd < 0) {
        return null;
      }

      const candidate = new Uint8Array(buffer.splice(0, crlfEnd + 1));
      try {
        const parsed = ModbusFrame.parseResponse(candidate, 'Ascii');
        if (expectedUnitId !== undefined && parsed.unitId !== expectedUnitId) {
          continue;
        }
        if (
          expectedFn !== undefined &&
          parsed.functionCode !== expectedFn &&
          parsed.functionCode !== (expectedFn | 0x80)
        ) {
          continue;
        }
        return candidate;
      } catch {
        // discard malformed ASCII frame
      }
    }
    return null;
  }

  // Modbus RTU stream accumulator
  while (buffer.length >= 4) {
    const respFn = buffer[1];
    let expectedLen = 0;

    if ((respFn & 0x80) !== 0) {
      expectedLen = 5;
    } else if (respFn === 1 || respFn === 2 || respFn === 3 || respFn === 4) {
      if (buffer.length < 3) return null;
      const byteCount = buffer[2];
      const validByteCount =
        respFn === 3 || respFn === 4
          ? byteCount >= 2 && byteCount <= 250 && byteCount % 2 === 0
          : byteCount >= 1 && byteCount <= 250;
      if (!validByteCount) {
        buffer.shift();
        continue;
      }
      expectedLen = 5 + byteCount;
    } else if (respFn === 5 || respFn === 6 || respFn === 15 || respFn === 16) {
      expectedLen = 8;
    } else {
      const maxProbe = Math.min(buffer.length, 64);
      for (let len = 4; len <= maxProbe; len++) {
        const probe = new Uint8Array(buffer.slice(0, len));
        if (Crc16.verify(probe)) {
          expectedLen = len;
          break;
        }
      }
      if (expectedLen === 0) {
        if (buffer.length >= 8) {
          buffer.shift();
          continue;
        }
        return null;
      }
    }

    if (buffer.length < expectedLen) {
      return null;
    }

    const candidate = new Uint8Array(buffer.slice(0, expectedLen));
    if (!Crc16.verify(candidate)) {
      buffer.shift();
      continue;
    }

    buffer.splice(0, expectedLen);
    if (expectedUnitId !== undefined && candidate[0] !== expectedUnitId) {
      continue;
    }
    if (
      expectedFn !== undefined &&
      respFn !== expectedFn &&
      respFn !== (expectedFn | 0x80)
    ) {
      continue;
    }
    return candidate;
  }

  return null;
}

export function tryTakeSerialRequestFrame(
  buffer: number[],
  mode: ModbusServerTransportMode
): Uint8Array | null {
  const allowRtu = mode === 'SerialRtu' || mode === 'TcpAndSerial';
  const allowAscii = mode === 'SerialAscii' || mode === 'TcpAndSerial';

  while (buffer.length > 0) {
    if (buffer[0] === 0x3a && allowAscii) {
      let terminator = -1;
      for (let i = 1; i < buffer.length; i++) {
        if (buffer[i - 1] === 0x0d && buffer[i] === 0x0a) {
          terminator = i;
          break;
        }
      }
      if (terminator < 0) return null;
      return new Uint8Array(buffer.splice(0, terminator + 1));
    }

    if (!allowRtu) {
      if (allowAscii) {
        const colon = buffer.indexOf(0x3a);
        if (colon < 0) {
          buffer.length = 0;
          return null;
        }
        buffer.splice(0, colon);
        continue;
      }
      buffer.shift();
      continue;
    }

    if (buffer.length < 4) return null;
    const functionCode = buffer[1];
    let expectedLength = 0;

    if (
      functionCode === 1 ||
      functionCode === 2 ||
      functionCode === 3 ||
      functionCode === 4 ||
      functionCode === 5 ||
      functionCode === 6
    ) {
      expectedLength = 8;
    } else if (functionCode === 15 || functionCode === 16) {
      if (buffer.length < 7) return null;
      const qty = (buffer[4] << 8) | buffer[5];
      const bc = buffer[6];
      const validHeader =
        functionCode === 15
          ? qty >= 1 && qty <= 1968 && bc === Math.ceil(qty / 8)
          : qty >= 1 && qty <= 123 && bc === qty * 2;
      if (!validHeader) {
        buffer.shift();
        continue;
      }
      expectedLength = 9 + bc;
    } else {
      const maxProbe = Math.min(buffer.length, 64);
      for (let len = 4; len <= maxProbe; len++) {
        if (Crc16.verify(new Uint8Array(buffer.slice(0, len)))) {
          expectedLength = len;
          break;
        }
      }
      if (expectedLength === 0) {
        if (buffer.length >= 8) {
          buffer.shift();
          continue;
        }
        return null;
      }
    }

    if (buffer.length < expectedLength) return null;
    const candidate = new Uint8Array(buffer.slice(0, expectedLength));
    if (!Crc16.verify(candidate)) {
      buffer.shift();
      continue;
    }

    buffer.splice(0, expectedLength);
    return candidate;
  }

  return null;
}

export class ModbusTcpServerService {
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

    for (const oldDef of previousDefs) {
      const stillSame = validated.find((n) => isSameSpec(n, oldDef));
      if (!stillSame) {
        this.applyDefinition(oldDef, true);
      }
    }

    this.definitions = validated.sort((a, b) =>
      a.area === b.area ? a.address - b.address : a.area.localeCompare(b.area)
    );

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
      if (!responsePdu) {
        return null;
      }
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
          if (!responsePdu) {
            return null;
          }
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
        if (!responsePdu) {
          continue;
        }
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

  processRequest(unitId: number, request: Uint8Array): Uint8Array | null {
    if (request.length < 1) {
      return new Uint8Array([0x80, 0x03]);
    }
    const fn = request[0];
    const isBroadcast = unitId === 0;
    if (!isBroadcast && unitId !== this.station) {
      return new Uint8Array([fn | 0x80, 0x0b]);
    }

    if (isBroadcast && fn !== 5 && fn !== 6 && fn !== 15 && fn !== 16) {
      return null;
    }

    try {
      let result: Uint8Array;
      switch (fn) {
        case 1:
          result = this.readBits(fn, request, this.coils);
          break;
        case 2:
          result = this.readBits(fn, request, this.discreteInputs);
          break;
        case 3:
          result = this.readRegisters(fn, request, this.holdingRegisters);
          break;
        case 4:
          result = this.readRegisters(fn, request, this.inputRegisters);
          break;
        case 5:
          result = this.writeSingleCoil(request);
          break;
        case 6:
          result = this.writeSingleRegister(request);
          break;
        case 15:
          result = this.writeMultipleCoils(fn, request);
          break;
        case 16:
          result = this.writeMultipleRegisters(fn, request);
          break;
        default:
          result = new Uint8Array([fn | 0x80, 0x01]);
          break;
      }
      return isBroadcast ? null : result;
    } catch (err) {
      if (isBroadcast) {
        return null;
      }
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

export class CommunicationManager {
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
  private tcpSessionGeneration = 0;
  private manualClosing = false;

  private udpSocket: dgram.Socket | null = null;
  private udpParams: UdpParameters | null = null;

  private serialParams: SerialPortParameters | null = null;
  private serialRxBuffer: number[] = [];

  private rawTcpServer: net.Server | null = null;
  private rawTcpClients = new Map<
    string,
    { socket: net.Socket; session: TcpServerClientSession }
  >();
  private nextSessionCounter = 1;

  private dataListeners = new Set<(bytes: Uint8Array) => void>();
  private closeWaiters = new Set<(err: Error) => void>();
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

    for (const listener of Array.from(this.dataListeners)) {
      listener(bytes);
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
    if (!params.portName?.trim()) {
      throw new Error('串口名称不能为空');
    }
    if (!Number.isInteger(params.baudRate) || params.baudRate <= 0) {
      throw new Error('波特率必须大于 0');
    }
    if (params.dataBits < 5 || params.dataBits > 8) {
      throw new Error('数据位必须在 5 至 8 之间');
    }

    await this.disconnect(false);
    this.manualClosing = false;
    this.channelType = 'SerialPort';
    this.serialParams = { ...params, portName: params.portName.trim() };
    this.serialRxBuffer = [];
    this.channelInfo = `${this.serialParams.portName} (${params.baudRate},${params.dataBits},${params.parity},${params.stopBits}) [仿真]`;
    this.state = 'Connected';
    this.deviceInfo = `虚拟串口已就绪 (${this.serialParams.portName})`;
    this.emitState();
  }

  async connectTcp(params: TcpClientParameters): Promise<void> {
    if (!params.host?.trim()) {
      throw new Error('TCP 主机地址不能为空');
    }
    if (!Number.isInteger(params.port) || params.port < 1 || params.port > 65535) {
      throw new Error('TCP 端口必须在 1 至 65535 之间');
    }

    await this.disconnect(false);
    this.manualClosing = false;
    this.channelType = 'TcpClient';
    this.tcpParams = {
      ...params,
      host: params.host.trim(),
      connectTimeoutMs: Math.min(60000, Math.max(100, params.connectTimeoutMs || 3000)),
      reconnectIntervalMs: Math.min(60000, Math.max(200, params.reconnectIntervalMs || 3000)),
    };
    this.channelInfo = `TCP Client (${this.tcpParams.host}:${this.tcpParams.port})`;
    this.state = 'Connecting';
    this.deviceInfo = `正在连接 ${this.tcpParams.host}:${this.tcpParams.port}...`;
    this.emitState();

    const gen = ++this.tcpSessionGeneration;
    await this.openTcpSocket(this.tcpParams, gen);
  }

  private openTcpSocket(
    params: TcpClientParameters,
    generation: number
  ): Promise<void> {
    return new Promise((resolve, reject) => {
      if (this.manualClosing || generation !== this.tcpSessionGeneration) {
        reject(new Error('连接已取消'));
        return;
      }

      const socket = new net.Socket();
      socket.setNoDelay(true);
      let settled = false;

      const timeoutMs = Math.min(60000, Math.max(100, params.connectTimeoutMs || 3000));
      const connectTimer = setTimeout(() => {
        if (settled) return;
        settled = true;
        socket.removeAllListeners();
        socket.destroy();
        if (generation !== this.tcpSessionGeneration || this.manualClosing) {
          reject(new Error('连接已取消'));
          return;
        }
        const err = new Error(`TCP 连接超时 (${timeoutMs}ms)`);
        this.state = 'Faulted';
        this.emitError(err.message);
        if (params.autoReconnect) {
          this.scheduleReconnect(generation);
        }
        reject(err);
      }, timeoutMs);

      socket.connect(params.port, params.host, () => {
        clearTimeout(connectTimer);
        if (settled || this.manualClosing || generation !== this.tcpSessionGeneration) {
          socket.removeAllListeners();
          socket.destroy();
          if (!settled) {
            settled = true;
            reject(new Error('连接已取消'));
          }
          return;
        }
        settled = true;
        this.tcpSocket = socket;
        this.state = 'Connected';
        this.deviceInfo = `已连接 ${params.host}:${params.port}`;
        this.emitState();
        resolve();
      });

      socket.on('data', (chunk) => {
        if (generation !== this.tcpSessionGeneration || this.manualClosing) return;
        this.emitDataReceived(new Uint8Array(chunk));
      });

      socket.on('error', (err) => {
        clearTimeout(connectTimer);
        if (generation !== this.tcpSessionGeneration || this.manualClosing) {
          if (!settled) {
            settled = true;
            reject(err);
          }
          return;
        }

        if (!settled) {
          settled = true;
          socket.removeAllListeners();
          socket.destroy();
          this.state = 'Faulted';
          this.emitError(err.message);
          if (params.autoReconnect) {
            this.scheduleReconnect(generation);
          }
          reject(err);
        } else {
          this.state = 'Faulted';
          this.emitError(err.message);
          if (params.autoReconnect) {
            this.scheduleReconnect(generation);
          }
        }
      });

      socket.on('close', () => {
        clearTimeout(connectTimer);
        if (this.tcpSocket === socket) {
          this.tcpSocket = null;
        }
        if (generation !== this.tcpSessionGeneration || this.manualClosing) return;

        if (settled && this.state === 'Connected') {
          this.state = 'Faulted';
          this.emitError('远程主机已关闭连接 (EOF)');
          if (params.autoReconnect) {
            this.scheduleReconnect(generation);
          }
        }
      });
    });
  }

  private scheduleReconnect(generation: number): void {
    if (
      this.reconnectTimer ||
      this.manualClosing ||
      !this.tcpParams ||
      generation !== this.tcpSessionGeneration
    ) {
      return;
    }

    this.state = 'Reconnecting';
    this.deviceInfo = '正在等待自动重连...';
    this.emitState();

    const interval = Math.min(60000, Math.max(200, this.tcpParams.reconnectIntervalMs || 3000));
    this.reconnectTimer = setTimeout(async () => {
      this.reconnectTimer = null;
      if (
        this.manualClosing ||
        !this.tcpParams ||
        generation !== this.tcpSessionGeneration
      ) {
        return;
      }
      try {
        this.state = 'Connecting';
        this.emitState();
        await this.openTcpSocket(this.tcpParams, generation);
      } catch {
        // openTcpSocket handles scheduling next retry cleanly
      }
    }, interval);
  }

  async connectUdp(params: UdpParameters): Promise<void> {
    if (!params.remoteHost?.trim()) {
      throw new Error('UDP 远程主机不能为空');
    }
    if (!Number.isInteger(params.remotePort) || params.remotePort < 1 || params.remotePort > 65535) {
      throw new Error('UDP 远程端口必须在 1 至 65535 之间');
    }
    if (!Number.isInteger(params.localPort) || params.localPort < 0 || params.localPort > 65535) {
      throw new Error('UDP 本地端口必须在 0 至 65535 之间');
    }

    await this.disconnect(false);
    this.manualClosing = false;
    this.channelType = 'Udp';
    this.udpParams = { ...params, remoteHost: params.remoteHost.trim() };
    this.channelInfo = `UDP (${this.udpParams.remoteHost}:${this.udpParams.remotePort})`;
    this.state = 'Connecting';
    this.emitState();

    await new Promise<void>((resolve, reject) => {
      const socket = dgram.createSocket('udp4');
      let settled = false;

      socket.once('error', (err) => {
        if (!settled) {
          settled = true;
          try { socket.close(); } catch { /* ignore */ }
          this.state = 'Faulted';
          this.emitError(err.message);
          reject(err);
        }
      });

      socket.on('message', (msg) => {
        if (!this.manualClosing) {
          this.emitDataReceived(new Uint8Array(msg));
        }
      });

      const localPort = params.localPort > 0 ? params.localPort : 0;
      socket.bind(localPort, '0.0.0.0', () => {
        if (settled || this.manualClosing) {
          try { socket.close(); } catch { /* ignore */ }
          if (!settled) {
            settled = true;
            reject(new Error('UDP 绑定已取消'));
          }
          return;
        }
        settled = true;
        socket.removeAllListeners('error');
        socket.on('error', (err) => {
          if (!this.manualClosing) {
            this.state = 'Faulted';
            this.emitError(err.message);
          }
        });
        this.udpSocket = socket;
        this.state = 'Connected';
        this.deviceInfo = `UDP 就绪 -> ${this.udpParams!.remoteHost}:${this.udpParams!.remotePort}`;
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
    this.tcpSessionGeneration++;

    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    const disconnectErr = new Error('连接已断开');
    for (const rejectWaiter of Array.from(this.closeWaiters)) {
      rejectWaiter(disconnectErr);
    }
    this.closeWaiters.clear();

    if (this.tcpSocket) {
      const s = this.tcpSocket;
      this.tcpSocket = null;
      s.removeAllListeners();
      s.destroy();
    }
    this.tcpParams = null;

    if (this.udpSocket) {
      const u = this.udpSocket;
      this.udpSocket = null;
      u.removeAllListeners();
      await new Promise<void>((resolve) => {
        try {
          u.close(() => resolve());
        } catch {
          resolve();
        }
      });
    }
    this.udpParams = null;
    this.serialParams = null;
    this.serialRxBuffer = [];

    for (const { socket } of this.rawTcpClients.values()) {
      socket.removeAllListeners();
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
      const socket = this.tcpSocket;
      await new Promise<void>((resolve, reject) => {
        socket.write(Buffer.from(bytes), (err) => {
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
      const udp = this.udpSocket;
      const { remotePort, remoteHost } = this.udpParams;
      await new Promise<void>((resolve, reject) => {
        udp.send(Buffer.from(bytes), remotePort, remoteHost, (err) => {
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

    if (this.channelType === 'SerialPort' && this.serialParams) {
      const activeSerial = this.serialParams;
      this.stats.bytesSent += bytes.length;
      this.stats.packetsSent += 1;
      this.emitTraffic('TX', bytes);
      this.emitState();

      setTimeout(() => {
        if (this.state !== 'Connected' || this.channelType !== 'SerialPort') return;
        for (const b of bytes) {
          this.serialRxBuffer.push(b);
        }
        if (this.serialRxBuffer.length > 4096) {
          this.serialRxBuffer.splice(0, this.serialRxBuffer.length - 4096);
        }

        let handledModbusFrame = false;
        while (true) {
          const frame = tryTakeSerialRequestFrame(
            this.serialRxBuffer,
            this.modbusServer.serverMode
          );
          if (!frame) break;
          handledModbusFrame = true;
          const reply = this.modbusServer.handleSerialRequest(activeSerial, frame);
          if (reply) {
            this.emitDataReceived(reply);
          }
        }

        if (!handledModbusFrame && this.serialRxBuffer.length > 0) {
          const looksLikeModbusRtu = bytes.length >= 4 && Crc16.verify(bytes);
          const looksLikeModbusAscii =
            bytes.length >= 7 &&
            bytes[0] === 0x3a &&
            bytes[bytes.length - 2] === 0x0d &&
            bytes[bytes.length - 1] === 0x0a;
          if (!looksLikeModbusRtu && !looksLikeModbusAscii) {
            // Raw echo for non-Modbus debugging when server is off/non-matching
            const rawEcho = new Uint8Array(this.serialRxBuffer.splice(0));
            this.emitDataReceived(rawEcho);
          }
        }
      }, 15);
      return bytes.length;
    }

    throw new Error('活动通讯通道不可用');
  }

  exchange(
    request: Uint8Array,
    timeoutMs = 1000,
    retries = 2,
    transportHint?: ModbusTransport
  ): Promise<Uint8Array> {
    if (!request || request.length === 0) {
      return Promise.reject(new Error('Modbus 请求报文不能为空'));
    }

    const normalizedTimeoutMs = Math.min(30000, Math.max(100, Math.floor(timeoutMs) || 1000));
    const normalizedRetries = Math.min(
      10,
      Math.max(0, Number.isFinite(retries) ? Math.floor(retries) : 2)
    );
    const meta = extractRequestMeta(request, transportHint);

    const run = async (): Promise<Uint8Array> => {
      if (this.state !== 'Connected') {
        throw new Error('通讯链路未就绪或已断开');
      }

      if (
        meta &&
        meta.unitId === 0 &&
        (meta.fn === 5 || meta.fn === 6 || meta.fn === 15 || meta.fn === 16)
      ) {
        await this.send(request);
        await new Promise((r) => setTimeout(r, 35));
        return new Uint8Array(0);
      }

      const maxAttempts = Math.max(1, normalizedRetries + 1);
      let lastError: Error | null = null;

      for (let attempt = 0; attempt < maxAttempts; attempt++) {
        if (this.state !== 'Connected') {
          throw new Error('通讯链路已断开');
        }

        try {
          return await this.executeSingleAttempt(
            request,
            normalizedTimeoutMs,
            meta
          );
        } catch (err) {
          lastError = err instanceof Error ? err : new Error(String(err));
          if (this.manualClosing || (this.state as ChannelState) === 'Disconnected') {
            throw lastError;
          }
        }
      }

      throw lastError ?? new Error('Modbus 请求未收到响应');
    };

    const next = this.exchangeQueue.then(run, run);
    this.exchangeQueue = next.then(
      () => undefined,
      () => undefined
    );
    return next;
  }

  private async executeSingleAttempt(
    request: Uint8Array,
    timeoutMs: number,
    meta: RequestMeta | null
  ): Promise<Uint8Array> {
    const transport: ModbusTransport =
      meta?.transport ?? (this.channelType === 'TcpClient' ? 'Tcp' : 'Rtu');
    const accumulator: number[] = [];

    return await new Promise<Uint8Array>((resolve, reject) => {
      let finished = false;
      let timer: NodeJS.Timeout | null = null;

      const cleanup = () => {
        if (timer) {
          clearTimeout(timer);
          timer = null;
        }
        this.dataListeners.delete(onData);
        this.closeWaiters.delete(onDisconnect);
      };

      const onDisconnect = (err: Error) => {
        if (finished) return;
        finished = true;
        cleanup();
        reject(err);
      };

      const onData = (chunk: Uint8Array) => {
        if (finished) return;
        for (let i = 0; i < chunk.length; i++) {
          accumulator.push(chunk[i]);
        }
        if (accumulator.length > 16384) {
          accumulator.splice(0, accumulator.length - 16384);
        }

        const matched = tryExtractMatchingFrame(
          accumulator,
          transport,
          meta?.transactionId,
          meta?.unitId,
          meta?.fn
        );
        if (matched) {
          finished = true;
          cleanup();
          resolve(matched);
        }
      };

      timer = setTimeout(() => {
        if (finished) return;
        finished = true;
        cleanup();
        reject(new Error(`等待响应超时 (${timeoutMs}ms)`));
      }, timeoutMs);

      this.dataListeners.add(onData);
      this.closeWaiters.add(onDisconnect);

      this.send(request).catch((err) => {
        if (finished) return;
        finished = true;
        cleanup();
        reject(err instanceof Error ? err : new Error(String(err)));
      });
    });
  }
}
