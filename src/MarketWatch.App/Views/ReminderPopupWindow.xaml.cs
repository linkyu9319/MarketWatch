using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MarketWatch.App.Services;
using MarketWatch.App.ViewModels;

namespace MarketWatch.App.Views;

public partial class ReminderPopupWindow : Window
{
    private readonly ReminderPopupViewModel _vm;
    private readonly DispatcherTimer _tickTimer;
    private readonly bool _playSound;
    private Storyboard? _breathingStoryboard;
    private Window? _backdropWindow;

    public ReminderPopupWindow(ReminderPopupViewModel viewModel, bool useFullScreenOverlay, bool playSound)
    {
        InitializeComponent();
        _vm = viewModel;
        _playSound = playSound;
        DataContext = _vm;

        _vm.RequestClose += OnRequestClose;

        ApplyStrengthVisuals();
        UpdateCutoffBannerVisuals();

        if (useFullScreenOverlay && _vm.IsStrong)
        {
            CreateBackdropWindow();
        }

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) =>
        {
            _vm.UpdateLiveState();
            UpdateCutoffBannerVisuals();
        };
    }

    private void ApplyStrengthVisuals()
    {
        if (_vm.IsStrong)
        {
            OuterFrame.MinWidth = 510;
            TitleTextBlock.FontSize = 23;
            PriceTextBlock.FontSize = 36;
            OuterFrame.BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBrush");
            OuterFrame.BorderThickness = new Thickness(3);
            BadgeBorder.Background = (System.Windows.Media.Brush)FindResource("DangerSubtleBrush");
            BadgeTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    private void UpdateCutoffBannerVisuals()
    {
        if (_vm.IsAfterCutoff)
        {
            CountdownBanner.Background = (System.Windows.Media.Brush)FindResource("DangerSubtleBrush");
            CountdownBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBrush");
            CountdownTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        else if (_vm.IsStrong)
        {
            CountdownBanner.Background = (System.Windows.Media.Brush)FindResource("DangerSubtleBrush");
            CountdownBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBrush");
            CountdownTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        else
        {
            CountdownBanner.Background = (System.Windows.Media.Brush)FindResource("WarningSubtleBrush");
            CountdownBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("WarningBrush");
            CountdownTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        }
    }

    private void CreateBackdropWindow()
    {
        var bounds = ScreenPlacementHelper.GetCurrentScreenBoundsDip(System.Windows.Application.Current?.MainWindow);
        _backdropWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(130, 8, 12, 20)),
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height
        };
        _backdropWindow.MouseDown += (_, _) => Activate();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _backdropWindow?.Show();
        ScreenPlacementHelper.CenterOnCurrentWorkArea(this);
        Topmost = true;

        if (_vm.EnableBreathingAnimation)
        {
            StartSlowBreathingAnimation();
        }

        if (_playSound)
        {
            try { SystemSounds.Exclamation.Play(); } catch { }
        }

        _tickTimer.Start();
    }

    private void StartSlowBreathingAnimation()
    {
        // Slow, gentle 1.6-second breathing cycle (safe, non-strobing)
        var anim = new DoubleAnimation
        {
            From = 0.15,
            To = 0.95,
            Duration = TimeSpan.FromMilliseconds(1600),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(anim, BreathingBorder);
        Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));

        _breathingStoryboard = new Storyboard();
        _breathingStoryboard.Children.Add(anim);
        _breathingStoryboard.Begin(this, isControllable: true);
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnRequestClose() => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _tickTimer.Stop();
        _breathingStoryboard?.Stop(this);
        _vm.RequestClose -= OnRequestClose;
        if (_backdropWindow is not null)
        {
            try { _backdropWindow.Close(); } catch { }
            _backdropWindow = null;
        }
    }
}
