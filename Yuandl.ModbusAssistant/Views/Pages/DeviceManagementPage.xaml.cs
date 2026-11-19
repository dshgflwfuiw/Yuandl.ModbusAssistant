using System.Windows.Controls;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Views.Pages;

public partial class DeviceManagementPage : Page, INavigableView<DeviceManagementViewModel>
{
    public DeviceManagementViewModel ViewModel { get; }

    public DeviceManagementPage(DeviceManagementViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
