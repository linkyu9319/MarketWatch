using System.Windows;
using System.Windows.Input;
using MarketWatch.App.Services;
using MarketWatch.App.ViewModels;

namespace MarketWatch.App.Views;

public partial class MiniFloatingWindow : Window
{
    private readonly AppStateCoordinator _coordinator;
    public event Action? OpenMainRequested;
    public event Action? HideRequested;

    public MiniFloatingWindow(MiniWindowViewModel viewModel, AppStateCoordinator coordinator)
    {
        InitializeComponent();
        DataContext = viewModel;
        _coordinator = coordinator;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ScreenPlacementHelper.PositionMiniWindowDefault(
            this,
            _coordinator.Settings.MiniWindowLeft,
            _coordinator.Settings.MiniWindowTop);
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                var left = Left;
                var top = Top;
                _ = _coordinator.UpdateSettingsAsync(s =>
                {
                    s.MiniWindowLeft = left;
                    s.MiniWindowTop = top;
                });
            }
            catch { }
        }
    }

    private void OnDoubleClickOpenMain(object sender, MouseButtonEventArgs e) => OpenMainRequested?.Invoke();
    private void OnOpenMainClick(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();
    private void OnHideClick(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
