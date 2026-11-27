using System.Windows;
using MarketWatch.App.ViewModels;

namespace MarketWatch.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
        Closed += (_, _) => _vm.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested() => Close();
}
