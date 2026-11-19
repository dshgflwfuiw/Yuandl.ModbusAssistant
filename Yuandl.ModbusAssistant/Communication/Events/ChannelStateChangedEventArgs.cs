using System;
using Yuandl.ModbusAssistant.Core.Enums;

namespace Yuandl.ModbusAssistant.Communication.Events;

/// <summary>
/// 通道运行状态改变时的事件参数
/// </summary>
public class ChannelStateChangedEventArgs : EventArgs
{
    public ChannelState OldState { get; }
    public ChannelState NewState { get; }
    public string Message { get; }

    public ChannelStateChangedEventArgs(ChannelState oldState, ChannelState newState, string message = "")
    {
        OldState = oldState;
        NewState = newState;
        Message = message;
    }
}
