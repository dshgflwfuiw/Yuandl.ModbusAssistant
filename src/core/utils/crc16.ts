const crcTable = new Uint16Array(256);

for (let i = 0; i < 256; i++) {
  let value = i;
  for (let j = 0; j < 8; j++) {
    if ((value & 1) !== 0) {
      value = ((value >>> 1) ^ 0xa001) & 0xffff;
    } else {
      value = (value >>> 1) & 0xffff;
    }
  }
  crcTable[i] = value;
}

export const Crc16 = {
  compute(buffer: Uint8Array): number {
    let crc = 0xffff;
    for (let i = 0; i < buffer.length; i++) {
      const index = (crc ^ buffer[i]) & 0xff;
      crc = ((crc >>> 8) ^ crcTable[index]) & 0xffff;
    }
    return crc;
  },

  verify(bufferWithCrc: Uint8Array): boolean {
    if (bufferWithCrc.length < 3) {
      return false;
    }
    const payloadLen = bufferWithCrc.length - 2;
    const calculated = Crc16.compute(bufferWithCrc.subarray(0, payloadLen));
    const expectedLow = calculated & 0xff;
    const expectedHigh = (calculated >>> 8) & 0xff;
    return (
      bufferWithCrc[payloadLen] === expectedLow &&
      bufferWithCrc[payloadLen + 1] === expectedHigh
    );
  },
};
