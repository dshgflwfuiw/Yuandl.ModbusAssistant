import type {
  ServerRegisterArea,
  ServerRegisterByteOrder,
  ServerRegisterDataType,
  ServerRegisterDefinition,
} from '../../core/types.js';

const VALID_AREAS: ServerRegisterArea[] = [
  'HoldingRegister',
  'InputRegister',
  'Coil',
  'DiscreteInput',
];

const VALID_TYPES: ServerRegisterDataType[] = [
  'Bit',
  'Int16',
  'UInt16',
  'Int32',
  'UInt32',
  'Float',
  'Double',
];

const VALID_ORDERS: ServerRegisterByteOrder[] = ['BigEndian', 'LittleEndian', 'WordSwap', 'ByteSwap'];

function parseStrictInt(value: string, min: number, max: number, label: string): number {
  const trimmed = value.trim();
  if (!/^-?\d+$/.test(trimmed)) {
    throw new Error(`${label} 值 "${value}" 不是有效的整数`);
  }
  const num = Number(trimmed);
  if (!Number.isSafeInteger(num) || num < min || num > max) {
    throw new Error(`${label} 值 "${value}" 超出范围 [${min}, ${max}]`);
  }
  return num;
}

function parseStrictFloat(value: string, label: string): number {
  const trimmed = value.trim();
  if (!trimmed || Number.isNaN(Number(trimmed))) {
    throw new Error(`${label} 值 "${value}" 不是有效的浮点数`);
  }
  const num = Number(trimmed);
  if (!Number.isFinite(num)) {
    throw new Error(`${label} 值 "${value}" 超出有限浮点范围`);
  }
  return num;
}

export const ServerRegisterCodec = {
  getWidth(type: ServerRegisterDataType): number {
    switch (type) {
      case 'Bit':
      case 'Int16':
      case 'UInt16':
        return 1;
      case 'Int32':
      case 'UInt32':
      case 'Float':
        return 2;
      case 'Double':
        return 4;
      default:
        throw new Error(`未知数据类型: ${String(type)}`);
    }
  },

  encodeRegisters(
    value: string,
    type: ServerRegisterDataType,
    byteOrder: ServerRegisterByteOrder
  ): number[] {
    const width = ServerRegisterCodec.getWidth(type);
    const bytes = new Uint8Array(width * 2);
    const view = new DataView(bytes.buffer);

    switch (type) {
      case 'Int16':
        view.setInt16(0, parseStrictInt(value, -32768, 32767, 'Int16'), false);
        break;
      case 'UInt16':
        view.setUint16(0, parseStrictInt(value, 0, 65535, 'UInt16'), false);
        break;
      case 'Int32':
        view.setInt32(0, parseStrictInt(value, -2147483648, 2147483647, 'Int32'), false);
        break;
      case 'UInt32':
        view.setUint32(0, parseStrictInt(value, 0, 4294967295, 'UInt32'), false);
        break;
      case 'Float':
        view.setFloat32(0, parseStrictFloat(value, 'Float'), false);
        break;
      case 'Double':
        view.setFloat64(0, parseStrictFloat(value, 'Double'), false);
        break;
      default:
        throw new Error(`数据类型 ${type} 不是寄存器数值类型`);
    }

    applyByteOrder(bytes, byteOrder);

    const registers: number[] = new Array(width);
    for (let i = 0; i < width; i++) {
      registers[i] = view.getUint16(i * 2, false);
    }
    return registers;
  },

  decodeRegisters(
    registers: number[],
    type: ServerRegisterDataType,
    byteOrder: ServerRegisterByteOrder
  ): string {
    const width = ServerRegisterCodec.getWidth(type);
    const bytes = new Uint8Array(width * 2);
    const view = new DataView(bytes.buffer);
    for (let i = 0; i < width; i++) {
      view.setUint16(i * 2, (registers[i] ?? 0) & 0xffff, false);
    }
    applyByteOrder(bytes, byteOrder);

    switch (type) {
      case 'Int16':
        return String(view.getInt16(0, false));
      case 'UInt16':
        return String(view.getUint16(0, false));
      case 'Int32':
        return String(view.getInt32(0, false));
      case 'UInt32':
        return String(view.getUint32(0, false));
      case 'Float':
        return String(Number(view.getFloat32(0, false).toPrecision(7)));
      case 'Double':
        return String(Number(view.getFloat64(0, false).toPrecision(12)));
      default:
        return String(registers[0] ?? 0);
    }
  },

  encodeBit(value: string): boolean {
    const normalized = value.trim().toLowerCase();
    if (normalized === 'true' || normalized === '1') return true;
    if (normalized === 'false' || normalized === '0') return false;
    throw new Error('线圈/离散输入的值必须为 true/false 或 1/0');
  },

  validate(definition: ServerRegisterDefinition): void {
    if (!definition) throw new Error('寄存器定义不能为空');
    if (!VALID_AREAS.includes(definition.area)) throw new Error('未知寄存器区域');
    if (!VALID_TYPES.includes(definition.dataType)) throw new Error('未知数据类型');
    if (!VALID_ORDERS.includes(definition.byteOrder)) throw new Error('未知字节序');
    if (!Number.isInteger(definition.address) || definition.address < 0 || definition.address > 65535) {
      throw new Error('起始地址必须在 0 至 65535 之间');
    }

    const bitArea =
      definition.area === 'Coil' || definition.area === 'DiscreteInput';
    if (bitArea !== (definition.dataType === 'Bit')) {
      throw new Error('线圈和离散输入必须使用 Bit 类型；寄存器区必须使用数值类型');
    }

    const width = ServerRegisterCodec.getWidth(definition.dataType);
    if (definition.address + width > 65536) {
      throw new Error('寄存器定义超出 Modbus 地址范围');
    }

    if (bitArea) {
      ServerRegisterCodec.encodeBit(definition.initialValue);
    } else {
      ServerRegisterCodec.encodeRegisters(
        definition.initialValue,
        definition.dataType,
        definition.byteOrder
      );
    }
  },

  validateNoOverlap(definitions: ServerRegisterDefinition[]): void {
    const byArea = new Map<ServerRegisterArea, ServerRegisterDefinition[]>();
    for (const def of definitions) {
      const arr = byArea.get(def.area) ?? [];
      arr.push(def);
      byArea.set(def.area, arr);
    }

    for (const [area, group] of byArea.entries()) {
      const ordered = [...group].sort((a, b) => a.address - b.address);
      for (let i = 1; i < ordered.length; i++) {
        const prev = ordered[i - 1];
        const prevEnd = prev.address + ServerRegisterCodec.getWidth(prev.dataType);
        if (ordered[i].address < prevEnd) {
          throw new Error(
            `${area} 地址 ${ordered[i].address} 与地址 ${prev.address} 的定义重叠`
          );
        }
      }
    }
  },
};

function applyByteOrder(bytes: Uint8Array, order: ServerRegisterByteOrder): void {
  if (order === 'LittleEndian') {
    bytes.reverse();
  } else if (order === 'WordSwap') {
    for (let left = 0, right = bytes.length - 2; left < right; left += 2, right -= 2) {
      [bytes[left], bytes[right]] = [bytes[right], bytes[left]];
      [bytes[left + 1], bytes[right + 1]] = [bytes[right + 1], bytes[left + 1]];
    }
  } else if (order === 'ByteSwap') {
    for (let i = 0; i + 1 < bytes.length; i += 2) {
      [bytes[i], bytes[i + 1]] = [bytes[i + 1], bytes[i]];
    }
  }
}
