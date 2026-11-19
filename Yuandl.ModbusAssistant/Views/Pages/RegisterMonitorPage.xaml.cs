using System.Windows.Controls;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Views.Pages;

public partial class RegisterMonitorPage : Page, INavigableView<RegisterMonitorViewModel>
{
    public RegisterMonitorViewModel ViewModel { get; }

    public RegisterMonitorPage(RegisterMonitorViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
