namespace Yuandl.ModbusAssistant.Protocol.Modbus;

public static class ModbusException
{
    public static string Describe(byte code) => code switch
    {
        1 => "非法功能", 2 => "非法数据地址", 3 => "非法数据值", 4 => "从站设备故障", 5 => "确认", 6 => "从站设备忙", 8 => "存储奇偶校验错误", 10 => "网关路径不可用", 11 => "网关目标设备未响应", _ => $"未知异常 (0x{code:X2})"
    };
}
