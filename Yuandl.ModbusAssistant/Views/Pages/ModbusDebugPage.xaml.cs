using System.Windows.Controls;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Views.Pages;

public partial class ModbusDebugPage : Page, INavigableView<ModbusDebugViewModel>
{
    public ModbusDebugViewModel ViewModel { get; }

    public ModbusDebugPage(ModbusDebugViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
