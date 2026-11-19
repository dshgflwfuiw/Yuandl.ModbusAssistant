using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Yuandl.ModbusAssistant.Core.Enums;
using Yuandl.ModbusAssistant.ViewModels.Pages;

namespace Yuandl.ModbusAssistant.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && (Invert ? v != Visibility.Visible : v == Visibility.Visible);
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}

public sealed class NavigationWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool expanded = value is bool b && b;
        return expanded ? new GridLength(220) : new GridLength(72);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ChineseValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;

        if (value is int intValue)
        {
            if (intValue is 7 or 8)
                return $"{intValue} 位";
            if (intValue is 110 or 300 or 1200 or 2400 or 4800 or 9600 or 19200 or 38400 or 57600 or 115200)
                return $"{intValue} bps";
            if (intValue is 15 or 30 or 60 or 120 or 300)
                return $"{intValue} 秒";
        }

        return value switch
        {
            Enum enumValue => enumValue switch
            {
                CommunicationType.SerialPort => "串口",
                CommunicationType.TcpClient => "TCP 客户端",
                CommunicationType.Udp => "UDP",
                CommunicationType.TcpServer => "TCP 服务端",
                DataEncoding.Hex => "十六进制",
                DataEncoding.Ascii => "ASCII",
                DataEncoding.Utf8 => "UTF-8",
                EndingDelimiter.None => "无结束符",
                EndingDelimiter.CR => "回车 CR",
                EndingDelimiter.LF => "换行 LF",
                EndingDelimiter.CRLF => "回车换行 CRLF",
                ModbusTransport.Rtu => "RTU",
                ModbusTransport.Ascii => "ASCII",
                ModbusTransport.Tcp => "TCP",
                RegisterDataType.Bool => "布尔 Bool",
                RegisterDataType.Int16 => "有符号 Int16",
                RegisterDataType.UInt16 => "无符号 UInt16",
                RegisterDataType.Int32 => "有符号 Int32",
                RegisterDataType.UInt32 => "无符号 UInt32",
                RegisterDataType.Float => "单精度 Float",
                RegisterDataType.Double => "双精度 Double",
                RegisterByteOrder.BigEndian => "大端序",
                RegisterByteOrder.LittleEndian => "小端序",
                ServerRegisterArea.HoldingRegister => "0x03/06/16 - 保持寄存器",
                ServerRegisterArea.InputRegister => "0x04 - 输入寄存器",
                ServerRegisterArea.Coil => "0x01/05/15 - 线圈",
                ServerRegisterArea.DiscreteInput => "0x02 - 离散输入",
                ServerRegisterDataType.Bit => "位",
                ServerRegisterDataType.Int16 => "有符号 Int16",
                ServerRegisterDataType.UInt16 => "无符号 UInt16",
                ServerRegisterDataType.Int32 => "有符号 Int32",
                ServerRegisterDataType.UInt32 => "无符号 UInt32",
                ServerRegisterDataType.Float => "单精度 Float",
                ServerRegisterDataType.Double => "双精度 Double",
                ServerRegisterByteOrder.BigEndian => "大端序",
                ServerRegisterByteOrder.LittleEndian => "小端序",
                ModbusServerTransportMode.Tcp => "TCP",
                ModbusServerTransportMode.SerialRtu => "串口 RTU",
                ModbusServerTransportMode.SerialAscii => "串口 ASCII",
                ModbusServerTransportMode.TcpAndSerial => "TCP + 串口",
                PacketCaptureTransportMode.Auto => "自动识别",
                PacketCaptureTransportMode.Tcp => "TCP",
                PacketCaptureTransportMode.Rtu => "RTU",
                PacketCaptureTransportMode.Ascii => "ASCII",
                MonitorDataSource.ModbusPolling => "Modbus 轮询采集",
                MonitorDataSource.ServerLiveListener => "内置服务端实时监听",
                MonitorViewLayout.Split => "分屏",
                MonitorViewLayout.ScopeOnly => "仅示波器",
                MonitorViewLayout.TableOnly => "仅数据表",
                TriggerEdgeMode.Rising => "上升沿",
                TriggerEdgeMode.Falling => "下降沿",
                Parity.None => "无校验",
                Parity.Odd => "奇校验",
                Parity.Even => "偶校验",
                Parity.Mark => "标记校验",
                Parity.Space => "空格校验",
                StopBits.One => "1 位",
                StopBits.OnePointFive => "1.5 位",
                StopBits.Two => "2 位",
                _ => enumValue.ToString()
            },
            byte byteValue => byteValue switch
            {
                1 => "01 - 读线圈",
                2 => "02 - 读离散输入",
                3 => "03 - 读保持寄存器",
                4 => "04 - 读输入寄存器",
                _ => byteValue.ToString()
            },
            string text => text switch
            {
                "Modbus TCP" => "Modbus TCP",
                "Modbus RTU" => "Modbus RTU",
                "Modbus ASCII" => "Modbus ASCII",
                "TCP Server" => "TCP 服务端",
                "UDP" => "UDP",
                _ => text
            },
            ModbusFunctionOption function => function.Label,
            _ => value.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch { /* fallback */ }
        }
        return Brushes.Cyan;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
