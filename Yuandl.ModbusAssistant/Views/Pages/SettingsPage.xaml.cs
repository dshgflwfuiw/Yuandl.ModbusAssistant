using System.Windows.Controls;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Controls;
using Yuandl.ThemeUI.Enums;
using Yuandl.ThemeUI.Managers;

namespace Yuandl.ModbusAssistant.Views.Pages;

public partial class SettingsPage : Page, INavigableView<SettingsViewModel>
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb)
        {
            var isLight = cb.SelectedIndex == 0;
            ApplicationThemeManager.Apply(isLight ? ApplicationTheme.Light : ApplicationTheme.Dark);
        }
    }
}
