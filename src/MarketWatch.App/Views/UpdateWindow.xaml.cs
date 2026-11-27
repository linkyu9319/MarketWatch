using System.Windows;
using System.Windows.Input;
using MarketWatch.App.ViewModels;

namespace MarketWatch.App.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateDialogViewModel _vm;

    public UpdateWindow(UpdateDialogViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
        Closed += (_, _) => _vm.CloseRequested -= OnCloseRequested;
    }

    private async void OnSaveTokenClick(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password;
        TokenBox.Clear();
        await _vm.SaveTokenAsync(token);
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.CloseCommand.Execute(null);
        }
    }

    private void OnCloseRequested() => Close();
}
