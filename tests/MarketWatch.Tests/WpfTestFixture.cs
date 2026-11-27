#if WINDOWS
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace MarketWatch.Tests;

[CollectionDefinition("WpfUi")]
public sealed class WpfUiCollection : ICollectionFixture<WpfApplicationFixture>
{
}

public sealed class WpfApplicationFixture : IDisposable
{
    private readonly Thread _staThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startupError;

    public Dispatcher Dispatcher { get; private set; } = null!;
    public System.Windows.Application App { get; private set; } = null!;

    public WpfApplicationFixture()
    {
        _staThread = new Thread(() =>
        {
            try
            {
                Dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));

                var app = System.Windows.Application.Current;
                if (app is null)
                {
                    // Use a clean WPF Application host so MarketWatch.App.App.OnStartup
                    // never runs or touches any live desktop instance's single-instance mutex.
                    app = new System.Windows.Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/MarketWatch;component/Themes/DarkTheme.xaml", UriKind.Absolute)
                    });
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/MarketWatch;component/Themes/SharedStyles.xaml", UriKind.Absolute)
                    });
                }

                App = app;
                _ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                _startupError = ex;
                _ready.Set();
            }
        });

        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
        _ready.Wait();

        if (_startupError is not null)
        {
            throw new InvalidOperationException("Failed to initialize WPF STA test host.", _startupError);
        }
    }

    public Task RunOnUiAsync(Action action) =>
        Dispatcher.InvokeAsync(action).Task;

    public Task RunOnUiAsync(Func<Task> asyncAction)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await asyncAction();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task DrainUiQueueAsync() =>
        Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;

    public void Dispose()
    {
        try
        {
            if (Dispatcher is not null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.InvokeShutdown();
            }
            _staThread.Join(TimeSpan.FromSeconds(3));
        }
        catch { }
        _ready.Dispose();
    }
}
#endif
