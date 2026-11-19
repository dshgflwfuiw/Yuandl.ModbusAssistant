using System.Collections.Specialized;
using System.Windows.Controls;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Views.Pages;

public partial class RawDebugPage : Page, INavigableView<RawDebugViewModel>
{
    public RawDebugViewModel ViewModel { get; }

    public RawDebugPage(RawDebugViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        ViewModel.Messages.CollectionChanged += Messages_CollectionChanged;
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel.IsAutoScroll)
        {
            if (MessagesList.Items.Count > 0)
            {
                MessagesList.ScrollIntoView(MessagesList.Items[^1]);
            }
        }
    }
}
