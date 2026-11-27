using System.IO;
using System.IO.Pipes;
using MarketWatch.Core.Interfaces;

namespace MarketWatch.App.Services;

/// <summary>
/// Ensures only one MarketWatch instance runs per user session, preventing duplicate alarms.
/// Secondary launches signal the primary instance over a local named pipe to activate its window.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\MarketWatch_SingleInstance_Mutex_v1";
    private const string PipeName = "MarketWatch_SingleInstance_Activate_v1";

    private readonly IAppLogger _logger;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenCts;
    private bool _ownsMutex;

    public event Action? ActivationRequested;

    public SingleInstanceService(IAppLogger logger)
    {
        _logger = logger;
    }

    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        StartListening();
        return true;
    }

    public static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client);
            writer.WriteLine("ACTIVATE");
            writer.Flush();
        }
        catch
        {
            // Existing instance may be starting up or closing
        }
    }

    private void StartListening()
    {
        _listenCts = new CancellationTokenSource();
        var ct = _listenCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var msg = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.Equals(msg, "ACTIVATE", StringComparison.Ordinal))
                    {
                        ActivationRequested?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warn("SingleInstance", $"Pipe listener error: {ex.Message}");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        try
        {
            _listenCts?.Cancel();
            _listenCts?.Dispose();
        }
        catch { }

        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
