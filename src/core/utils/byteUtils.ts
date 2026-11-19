import type { DataEncoding, EndingDelimiter } from '../types.js';

const HEX_CHARS = '0123456789ABCDEF';
const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder('utf-8', { fatal: false });

function parseHexNibble(ch: string): number {
  const code = ch.charCodeAt(0);
  if (code >= 48 && code <= 57) return code - 48; // 0-9
  if (code >= 65 && code <= 70) return code - 65 + 10; // A-F
  if (code >= 97 && code <= 102) return code - 97 + 10; // a-f
  return -1;
}

export const ByteUtils = {
  hexToBytes(hexString: string): Uint8Array {
    if (hexString === null || hexString === undefined) {
      throw new Error('输入不能为空');
    }

    const cleanChars: string[] = [];
    for (let i = 0; i < hexString.length; i++) {
      const c = hexString[i];
      if (/\s/.test(c) || c === ',' || c === '-' || c === ';' || c === ':') {
        continue;
      }
      if (
        c === '0' &&
        i + 1 < hexString.length &&
        (hexString[i + 1] === 'x' || hexString[i + 1] === 'X')
      ) {
        i++;
        continue;
      }
      cleanChars.push(c);
    }

    if (cleanChars.length === 0) {
      return new Uint8Array(0);
    }

    if (cleanChars.length % 2 !== 0) {
      throw new Error(
        `HEX 字符串字符数必须为偶数 (当前有效字符数: ${cleanChars.length})，请检查是否遗漏了高位或低位字符。`
      );
    }

    const byteCount = cleanChars.length / 2;
    const result = new Uint8Array(byteCount);
    for (let i = 0; i < byteCount; i++) {
      const high = parseHexNibble(cleanChars[i * 2]);
      const low = parseHexNibble(cleanChars[i * 2 + 1]);
      if (high === -1 || low === -1) {
        const invalidChar = high === -1 ? cleanChars[i * 2] : cleanChars[i * 2 + 1];
        throw new Error(
          `检测到非法的十六进制字符 '${invalidChar}'，有效字符范围为 [0-9, a-f, A-F]。`
        );
      }
      result[i] = (high << 4) | low;
    }

    return result;
  },

  bytesToHex(bytes: Uint8Array, separator = ' '): string {
    if (!bytes || bytes.length === 0) {
      return '';
    }
    const parts: string[] = new Array(bytes.length);
    for (let i = 0; i < bytes.length; i++) {
      const b = bytes[i];
      parts[i] = HEX_CHARS[b >> 4] + HEX_CHARS[b & 0x0f];
    }
    return parts.join(separator);
  },

  appendDelimiter(data: Uint8Array, delimiter: EndingDelimiter): Uint8Array {
    const base = data ?? new Uint8Array(0);
    let suffix: number[] = [];
    if (delimiter === 'CR') suffix = [0x0d];
    else if (delimiter === 'LF') suffix = [0x0a];
    else if (delimiter === 'CRLF') suffix = [0x0d, 0x0a];
    else return base;

    const combined = new Uint8Array(base.length + suffix.length);
    combined.set(base, 0);
    combined.set(suffix, base.length);
    return combined;
  },

  encodeInput(
    text: string,
    encoding: DataEncoding,
    delimiter: EndingDelimiter = 'None'
  ): Uint8Array {
    if (!text) {
      return ByteUtils.appendDelimiter(new Uint8Array(0), delimiter);
    }

    let rawBytes: Uint8Array;
    if (encoding === 'Hex') {
      rawBytes = ByteUtils.hexToBytes(text);
    } else if (encoding === 'Ascii') {
      rawBytes = new Uint8Array(text.length);
      for (let i = 0; i < text.length; i++) {
        const code = text.charCodeAt(i);
        rawBytes[i] = code <= 0x7f ? code : 0x3f;
      }
    } else if (encoding === 'Utf8') {
      rawBytes = textEncoder.encode(text);
    } else {
      throw new Error(`不支持的数据编码类型: ${String(encoding)}`);
    }

    return ByteUtils.appendDelimiter(rawBytes, delimiter);
  },

  decodeReceived(bytes: Uint8Array, encoding: DataEncoding): string {
    if (!bytes || bytes.length === 0) {
      return '';
    }
    if (encoding === 'Hex') {
      return ByteUtils.bytesToHex(bytes);
    }
    if (encoding === 'Ascii') {
      let out = '';
      for (let i = 0; i < bytes.length; i++) {
        out += bytes[i] <= 0x7f ? String.fromCharCode(bytes[i]) : '?';
      }
      return out;
    }
    return textDecoder.decode(bytes);
  },
};
