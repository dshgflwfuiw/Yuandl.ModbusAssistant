export const LrcUtils = {
  compute(buffer: Uint8Array): number {
    let sum = 0;
    for (let i = 0; i < buffer.length; i++) {
      sum = (sum + buffer[i]) & 0xff;
    }
    return (-sum) & 0xff;
  },

  verify(bufferWithLrc: Uint8Array): boolean {
    if (bufferWithLrc.length === 0) {
      return false;
    }
    let sum = 0;
    for (let i = 0; i < bufferWithLrc.length; i++) {
      sum = (sum + bufferWithLrc[i]) & 0xff;
    }
    return sum === 0;
  },
};
