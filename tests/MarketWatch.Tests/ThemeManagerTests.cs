#if WINDOWS
using System.Drawing;
using System.Windows.Controls;
using System.Windows.Media;
using MarketWatch.App.Services;
using MarketWatch.Core.Models;
using Xunit;
using AppThemeMode = MarketWatch.Core.Models.ThemeMode;

namespace MarketWatch.Tests;

[Collection("WpfUi")]
public sealed class ThemeManagerTests
{
    [Fact]
    public void Apply_SwitchesGlobalApplicationResourcesBetweenDarkAndLight_AndLoadsVegetableAssets()
    {
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current as MarketWatch.App.App;
                if (app is null)
                {
                    app = new MarketWatch.App.App();
                    app.InitializeComponent();
                }

                var themeManager = new ThemeManager();
                var probe = new Border();
                probe.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
                var styledButton = new Button { Content = "Test" };
                styledButton.Style = (System.Windows.Style)app.Resources["SubtleButtonStyle"];
                probe.Child = styledButton;

                var hostWindow = new System.Windows.Window
                {
                    Content = probe,
                    Width = 200,
                    Height = 120,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = System.Windows.WindowStyle.None,
                    AllowsTransparency = true,
                    Opacity = 0,
                    Left = -30000,
                    Top = -30000
                };
                hostWindow.SetResourceReference(System.Windows.Window.BackgroundProperty, "AppBackgroundBrush");

                try
                {
                    hostWindow.Show();

                    // Initial App.xaml loads DarkTheme
                    themeManager.Apply(AppThemeMode.Dark, ColorScheme.ChinaRedUpGreenDown);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x0D, 0x11, 0x17),
                        ((SolidColorBrush)app.Resources["AppBackgroundBrush"]).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x0D, 0x11, 0x17),
                        ((SolidColorBrush)hostWindow.Background).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x0D, 0x11, 0x17),
                        ((SolidColorBrush)probe.Background).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x1E, 0x27, 0x36),
                        ((SolidColorBrush)styledButton.Background).Color);

                    // Switch to Light: must replace App.xaml's DarkTheme so dynamic resources update globally
                    themeManager.Apply(AppThemeMode.Light, ColorScheme.ChinaRedUpGreenDown);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0xF4, 0xF7, 0xFB),
                        ((SolidColorBrush)app.Resources["AppBackgroundBrush"]).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF),
                        ((SolidColorBrush)app.Resources["CardBackgroundBrush"]).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A),
                        ((SolidColorBrush)app.Resources["TextPrimaryBrush"]).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0xF4, 0xF7, 0xFB),
                        ((SolidColorBrush)hostWindow.Background).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0xF4, 0xF7, 0xFB),
                        ((SolidColorBrush)probe.Background).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0xF8, 0xFA, 0xFC),
                        ((SolidColorBrush)styledButton.Background).Color);

                    // Exactly 1 theme dictionary + SharedStyles (no duplicate shadowing dictionaries)
                    Assert.Equal(2, app.Resources.MergedDictionaries.Count);

                    // Switch back to Dark
                    themeManager.Apply(AppThemeMode.Dark, ColorScheme.ChinaRedUpGreenDown);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x0D, 0x11, 0x17),
                        ((SolidColorBrush)hostWindow.Background).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x0D, 0x11, 0x17),
                        ((SolidColorBrush)probe.Background).Color);
                    Assert.Equal(
                        System.Windows.Media.Color.FromRgb(0x1E, 0x27, 0x36),
                        ((SolidColorBrush)styledButton.Background).Color);
                    Assert.Equal(2, app.Resources.MergedDictionaries.Count);
                }
                finally
                {
                    hostWindow.Close();
                }

                // Verify embedded VSRead vegetable PNG and ICO assets resolve cleanly
                var pngStream = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/MarketWatch;component/Assets/vegetable.png"));
                Assert.NotNull(pngStream?.Stream);
                Assert.True(pngStream!.Stream.Length > 1000);
                pngStream.Stream.Dispose();

                var icoStream = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/MarketWatch;component/Assets/vegetable.ico"));
                Assert.NotNull(icoStream?.Stream);
                using (icoStream!.Stream)
                using (var icon = new Icon(icoStream.Stream, new Size(32, 32)))
                {
                    Assert.Equal(32, icon.Width);
                    Assert.Equal(32, icon.Height);
                }
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadError is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA WPF theme/asset test failed: {threadError}");
        }
    }
}
#endif
