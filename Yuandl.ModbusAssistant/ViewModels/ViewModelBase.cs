using CommunityToolkit.Mvvm.ComponentModel;

namespace Yuandl.ModbusAssistant.ViewModels;

/// <summary>
/// 基础 ViewModel，继承自 ObservableObject，提供属性通知与基础能力
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private bool isBusy;
}
