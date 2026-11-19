using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Yuandl.ModbusAssistant.Views;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private bool _isNavigationExpanded = true;

    public MainWindow()
    {
        InitializeComponent();
        ToggleNavigationCommand = new RelayCommand(ToggleNavigation);
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
