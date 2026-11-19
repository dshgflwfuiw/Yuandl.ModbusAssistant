import type { RegisterByteOrder, RegisterDataType } from '../../core/types.js';

export const RegisterValueConverter = {
  getWidth(type: RegisterDataType): number {
    if (type === 'Int32' || type === 'UInt32' || type === 'Float') return 2;
    if (type === 'Double') return 4;
    return 1;
  },

  convert(
    registers: number[],
    type: RegisterDataType,
    order: RegisterByteOrder = 'BigEndian',
    scale = 1
  ): number {
    if (!registers.length) {
      throw new Error('至少需要一个寄存器');
    }

    const width = RegisterValueConverter.getWidth(type);
    if (registers.length < width) {
      throw new Error(`${type} 至少需要 ${width} 个寄存器`);
    }

    const bytes = new Uint8Array(width * 2);
    for (let i = 0; i < width; i++) {
      bytes[i * 2] = (registers[i] >>> 8) & 0xff;
      bytes[i * 2 + 1] = registers[i] & 0xff;
    }

    applyByteOrder(bytes, order);

    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let rawValue: number;

    switch (type) {
      case 'Bool':
        rawValue = view.getUint16(0, false) !== 0 ? 1 : 0;
        break;
      case 'Int16':
        rawValue = view.getInt16(0, false);
        break;
      case 'UInt16':
        rawValue = view.getUint16(0, false);
        break;
      case 'Int32':
        rawValue = view.getInt32(0, false);
        break;
      case 'UInt32':
        rawValue = view.getUint32(0, false);
        break;
      case 'Float':
        rawValue = view.getFloat32(0, false);
        break;
      case 'Double':
        rawValue = view.getFloat64(0, false);
        break;
      default:
        throw new Error(`不支持的寄存器类型: ${String(type)}`);
    }

    const scaled = rawValue * scale;
    return Number.isFinite(scaled) ? Number(scaled.toPrecision(12)) : scaled;
  },
};

function applyByteOrder(bytes: Uint8Array, order: RegisterByteOrder): void {
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
