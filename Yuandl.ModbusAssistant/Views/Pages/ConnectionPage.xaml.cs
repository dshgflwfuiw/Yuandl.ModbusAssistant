using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfScrollViewer = System.Windows.Controls.ScrollViewer;
using Yuandl.ModbusAssistant.ViewModels.Pages;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Views.Pages;

public partial class ConnectionPage : Page, INavigableView<ConnectionViewModel>
{
    public ConnectionViewModel ViewModel { get; }

    public ConnectionPage(ConnectionViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    private void ServerRegistersList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var listScrollViewer = FindVisualChild<WpfScrollViewer>(ServerRegistersList);
        if (listScrollViewer is not null)
        {
            var canScrollInsideList = e.Delta > 0
                ? listScrollViewer.VerticalOffset > 0
                : listScrollViewer.VerticalOffset < listScrollViewer.ScrollableHeight;

            if (canScrollInsideList)
            {
                if (e.Delta > 0) listScrollViewer.LineUp();
                else listScrollViewer.LineDown();
                e.Handled = true;
                return;
            }
        }

        PageScrollViewer.ScrollToVerticalOffset(PageScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }

        return null;
    }
}
