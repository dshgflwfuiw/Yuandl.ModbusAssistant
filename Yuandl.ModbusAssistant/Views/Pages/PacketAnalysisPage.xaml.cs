using System.Windows.Controls;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Views.Pages;

public partial class PacketAnalysisPage : Page, INavigableView<PacketAnalysisViewModel>
{
    public PacketAnalysisViewModel ViewModel { get; }

    public PacketAnalysisPage(PacketAnalysisViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
