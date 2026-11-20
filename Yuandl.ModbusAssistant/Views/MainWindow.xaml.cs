using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Yuandl.ModbusAssistant.ViewModels;
using Yuandl.ThemeUI.Appearance;
using Yuandl.ThemeUI.Controls;

namespace Yuandl.ModbusAssistant.Views;

public partial class MainWindow : FluentWindow, INotifyPropertyChanged
{
    private bool _isNavigationExpanded = true;
    private MainViewModel? _boundVm;

    public MainWindow()
    {
        InitializeComponent();
        ToggleNavigationCommand = new RelayCommand(ToggleNavigation);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.RawDebug.Messages.CollectionChanged -= OnRawMessagesChanged;
            _boundVm.PacketAnalysis.CapturedPackets.CollectionChanged -= OnCapturedPacketsChanged;
        }

        _boundVm = e.NewValue as MainViewModel;
        if (_boundVm != null)
        {
            _boundVm.RawDebug.Messages.CollectionChanged += OnRawMessagesChanged;
            _boundVm.PacketAnalysis.CapturedPackets.CollectionChanged += OnCapturedPacketsChanged;
        }
    }

    private void OnRawMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_boundVm?.RawDebug.AutoScroll == true && RawMessagesGrid.Items.Count > 0)
            RawMessagesGrid.ScrollIntoView(RawMessagesGrid.Items[0]);
    }

    private void OnCapturedPacketsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_boundVm?.PacketAnalysis.AutoScroll == true && CapturedPacketsGrid.Items.Count > 0)
            CapturedPacketsGrid.ScrollIntoView(CapturedPacketsGrid.Items[0]);
    }

    public bool IsNavigationExpanded
    {
        get => _isNavigationExpanded;
        private set
        {
            if (_isNavigationExpanded == value) return;
            _isNavigationExpanded = value;
            OnPropertyChanged();
        }
    }

    public ICommand ToggleNavigationCommand { get; }

    private void ToggleNavigation()
    {
        IsNavigationExpanded = !IsNavigationExpanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
