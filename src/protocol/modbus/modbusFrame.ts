import type { ModbusTransport } from '../../core/types.js';
import { ByteUtils } from '../../core/utils/byteUtils.js';
import { Crc16 } from '../../core/utils/crc16.js';
import { LrcUtils } from '../../core/utils/lrcUtils.js';

export interface ModbusResponse {
  transactionId?: number;
  unitId: number;
  functionCode: number;
  payload: Uint8Array;
  isException: boolean;
  exceptionCode: number | null;
  registers: number[];
}

export interface ModbusParsedRequest {
  transactionId?: number;
  unitId: number;
  functionCode: number;
  address: number;
  quantity?: number;
  values?: number[];
}

function extractRegisters(payload: Uint8Array): number[] {
  const data =
    payload.length > 0 && payload[0] === payload.length - 1
      ? payload.subarray(1)
      : payload;
  if (data.length % 2 !== 0) {
    return [];
  }
  const result: number[] = new Array(data.length / 2);
  for (let i = 0; i < result.length; i++) {
    result[i] = (data[i * 2] << 8) | data[i * 2 + 1];
  }
  return result;
}

function unwrapTransport(
  frame: Uint8Array,
  transport: ModbusTransport
): { pdu: Uint8Array; transactionId?: number } {
  if (transport === 'Rtu') {
    if (!Crc16.verify(frame)) {
      throw new Error('Modbus RTU CRC 校验失败');
    }
    return { pdu: frame.subarray(0, frame.length - 2) };
  }

  if (transport === 'Tcp') {
    if (frame.length < 8) {
      throw new Error('Modbus TCP 帧长度不足');
    }
    const transactionId = (frame[0] << 8) | frame[1];
    const protocolId = (frame[2] << 8) | frame[3];
    if (protocolId !== 0) {
      throw new Error('Modbus TCP 协议标识不匹配');
    }
    const len = (frame[4] << 8) | frame[5];
    if (len !== frame.length - 6) {
      throw new Error('Modbus TCP 长度字段不匹配');
    }
    return { pdu: frame.subarray(6), transactionId };
  }

  let text = '';
  for (let i = 0; i < frame.length; i++) {
    text += String.fromCharCode(frame[i]);
  }
  if (!text.startsWith(':') || !text.endsWith('\r\n')) {
    throw new Error('Modbus ASCII 帧格式错误');
  }
  const hex = ByteUtils.hexToBytes(text.slice(1, -2));
  if (!LrcUtils.verify(hex)) {
    throw new Error('Modbus ASCII LRC 校验失败');
  }
  return { pdu: hex.subarray(0, hex.length - 1) };
}

function extractBits(payload: Uint8Array, quantity: number): boolean[] {
  const data =
    payload.length > 0 && payload[0] === payload.length - 1
      ? payload.subarray(1)
      : payload;
  const maxBits = data.length * 8;
  const count = Math.min(Math.max(0, quantity), maxBits);
  const bits: boolean[] = new Array(count);
  for (let i = 0; i < count; i++) {
    bits[i] = (data[Math.floor(i / 8)] & (1 << (i % 8))) !== 0;
  }
  return bits;
}

export const ModbusFrame = {
  extractBits,
  buildRequest(
    unitId: number,
    functionCode: number,
    address: number,
    quantity: number,
    transport: ModbusTransport = 'Rtu',
    transactionId = 1
  ): Uint8Array {
    if (quantity <= 0 || quantity > 0xffff) {
      throw new Error('数量必须在 1 至 65535 之间');
    }
    const pdu = new Uint8Array(6);
    pdu[0] = unitId & 0xff;
    pdu[1] = functionCode & 0xff;
    pdu[2] = (address >>> 8) & 0xff;
    pdu[3] = address & 0xff;
    pdu[4] = (quantity >>> 8) & 0xff;
    pdu[5] = quantity & 0xff;
    return ModbusFrame.encode(pdu, transport, transactionId);
  },

  buildWriteSingle(
    unitId: number,
    functionCode: number,
    address: number,
    value: number,
    transport: ModbusTransport = 'Rtu',
    transactionId = 1
  ): Uint8Array {
    const pdu = new Uint8Array(6);
    pdu[0] = unitId & 0xff;
    pdu[1] = functionCode & 0xff;
    pdu[2] = (address >>> 8) & 0xff;
    pdu[3] = address & 0xff;
    pdu[4] = (value >>> 8) & 0xff;
    pdu[5] = value & 0xff;
    return ModbusFrame.encode(pdu, transport, transactionId);
  },

  buildWriteMultiple(
    unitId: number,
    functionCode: number,
    address: number,
    values: number[],
    transport: ModbusTransport = 'Rtu',
    transactionId = 1
  ): Uint8Array {
    if (functionCode === 15) {
      if (!values.length || values.length > 1968) {
        throw new Error('写入线圈数量必须在 1 至 1968 之间');
      }
      const quantity = values.length;
      const byteCount = Math.ceil(quantity / 8);
      const pdu = new Uint8Array(7 + byteCount);
      pdu[0] = unitId & 0xff;
      pdu[1] = functionCode & 0xff;
      pdu[2] = (address >>> 8) & 0xff;
      pdu[3] = address & 0xff;
      pdu[4] = (quantity >>> 8) & 0xff;
      pdu[5] = quantity & 0xff;
      pdu[6] = byteCount & 0xff;
      for (let i = 0; i < quantity; i++) {
        if (values[i] !== 0) {
          pdu[7 + Math.floor(i / 8)] |= 1 << (i % 8);
        }
      }
      return ModbusFrame.encode(pdu, transport, transactionId);
    }

    if (!values.length || values.length > 123) {
      throw new Error('写入寄存器数量必须在 1 至 123 之间');
    }

    const pdu = new Uint8Array(7 + values.length * 2);
    pdu[0] = unitId & 0xff;
    pdu[1] = functionCode & 0xff;
    pdu[2] = (address >>> 8) & 0xff;
    pdu[3] = address & 0xff;
    pdu[4] = (values.length >>> 8) & 0xff;
    pdu[5] = values.length & 0xff;
    pdu[6] = (values.length * 2) & 0xff;
    for (let i = 0; i < values.length; i++) {
      pdu[7 + i * 2] = (values[i] >>> 8) & 0xff;
      pdu[8 + i * 2] = values[i] & 0xff;
    }
    return ModbusFrame.encode(pdu, transport, transactionId);
  },

  parseMultiValues(text: string, isCoils: boolean): number[] {
    const tokens = text
      .split(/[\s,;，；\r\n]+/)
      .map((s) => s.trim())
      .filter(Boolean);
    if (tokens.length === 0) {
      throw new Error('请至少输入一个数值');
    }

    if (isCoils) {
      if (tokens.length > 1968) {
        throw new Error('批量写入线圈最多支持 1968 个');
      }
      return tokens.map((tok) => {
        const lower = tok.toLowerCase();
        if (lower === '1' || lower === 'true' || lower === 'on') return 1;
        if (lower === '0' || lower === 'false' || lower === 'off') return 0;
        throw new Error(`无效的线圈值 "${tok}"，请使用 1/0 或 true/false`);
      });
    }

    if (tokens.length > 123) {
      throw new Error('批量写入寄存器最多支持 123 个');
    }
    return tokens.map((tok) => {
      const parsed = /^0x[0-9a-f]+$/i.test(tok)
        ? Number.parseInt(tok, 16)
        : /^-?\d+$/.test(tok)
          ? Number.parseInt(tok, 10)
          : Number.NaN;
      if (!Number.isInteger(parsed) || parsed < 0 || parsed > 65535) {
        throw new Error(`无效的寄存器值 "${tok}"，范围为 0~65535 或 0x0000~0xFFFF`);
      }
      return parsed;
    });
  },

  encode(
    rtuPdu: Uint8Array,
    transport: ModbusTransport,
    transactionId = 1
  ): Uint8Array {
    if (transport === 'Rtu') {
      const result = new Uint8Array(rtuPdu.length + 2);
      result.set(rtuPdu, 0);
      const crc = Crc16.compute(rtuPdu);
      result[result.length - 2] = crc & 0xff;
      result[result.length - 1] = (crc >>> 8) & 0xff;
      return result;
    }

    if (transport === 'Tcp') {
      const result = new Uint8Array(rtuPdu.length + 6);
      result[0] = (transactionId >>> 8) & 0xff;
      result[1] = transactionId & 0xff;
      result[2] = 0;
      result[3] = 0;
      result[4] = (rtuPdu.length >>> 8) & 0xff;
      result[5] = rtuPdu.length & 0xff;
      result.set(rtuPdu, 6);
      return result;
    }

    const ascii = ByteUtils.bytesToHex(rtuPdu, '');
    const lrc = LrcUtils.compute(rtuPdu);
    const lrcHex = lrc.toString(16).toUpperCase().padStart(2, '0');
    const frameStr = `:${ascii}${lrcHex}\r\n`;
    const bytes = new Uint8Array(frameStr.length);
    for (let i = 0; i < frameStr.length; i++) {
      bytes[i] = frameStr.charCodeAt(i);
    }
    return bytes;
  },

  parseRequest(frame: Uint8Array, transport: ModbusTransport): ModbusParsedRequest {
    const { pdu, transactionId } = unwrapTransport(frame, transport);
    if (pdu.length < 6) {
      throw new Error('Modbus 请求长度不足');
    }
    const unitId = pdu[0];
    const functionCode = pdu[1];
    const address = (pdu[2] << 8) | pdu[3];

    if (functionCode === 5 || functionCode === 6) {
      const val = (pdu[4] << 8) | pdu[5];
      return {
        transactionId,
        unitId,
        functionCode,
        address,
        quantity: 1,
        values: [val],
      };
    }

    const quantity = (pdu[4] << 8) | pdu[5];
    if (functionCode === 15 || functionCode === 16) {
      if (pdu.length < 7) throw new Error('Modbus 多写请求长度不足');
      const byteCount = pdu[6];
      if (pdu.length !== 7 + byteCount) {
        throw new Error('Modbus 多写请求字节数不匹配');
      }
      const values: number[] = [];
      if (functionCode === 15) {
        for (let i = 0; i < quantity; i++) {
          const bit = (pdu[7 + Math.floor(i / 8)] & (1 << (i % 8))) !== 0 ? 1 : 0;
          values.push(bit);
        }
      } else {
        for (let i = 0; i < quantity && 7 + i * 2 + 1 < pdu.length; i++) {
          values.push((pdu[7 + i * 2] << 8) | pdu[8 + i * 2]);
        }
      }
      return { transactionId, unitId, functionCode, address, quantity, values };
    }

    return { transactionId, unitId, functionCode, address, quantity };
  },

  parseResponse(frame: Uint8Array, transport: ModbusTransport): ModbusResponse {
    const { pdu, transactionId } = unwrapTransport(frame, transport);
    if (pdu.length < 2) {
      throw new Error('Modbus 响应长度不足');
    }

    const isException = (pdu[1] & 0x80) !== 0;
    if (isException) {
      return {
        transactionId,
        unitId: pdu[0],
        functionCode: pdu[1],
        payload: new Uint8Array(0),
        isException: true,
        exceptionCode: pdu.length > 2 ? pdu[2] : null,
        registers: [],
      };
    }

    const payload = pdu.subarray(2);
    return {
      transactionId,
      unitId: pdu[0],
      functionCode: pdu[1],
      payload,
      isException: false,
      exceptionCode: null,
      registers: extractRegisters(payload),
    };
  },
};
