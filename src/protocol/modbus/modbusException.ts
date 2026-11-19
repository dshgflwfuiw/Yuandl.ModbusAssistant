export const ModbusException = {
  describe(code: number): string {
    switch (code) {
      case 1:
        return '非法功能';
      case 2:
        return '非法数据地址';
      case 3:
        return '非法数据值';
      case 4:
        return '从站设备故障';
      case 5:
        return '确认';
      case 6:
        return '从站设备忙';
      case 8:
        return '存储奇偶校验错误';
      case 10:
        return '网关路径不可用';
      case 11:
        return '网关目标设备未响应';
      default:
        return `未知异常 (0x${code.toString(16).toUpperCase().padStart(2, '0')})`;
    }
  },
};
