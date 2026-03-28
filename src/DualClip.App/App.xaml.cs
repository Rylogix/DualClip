using System.Threading;
namespace DualClip.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\DualClip.App.SingleInstance";
    private const string RestoreWindowEventName = @"Local\DualClip.App.RestoreWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _restoreWindowEvent;
    private RegisteredWaitHandle? _restoreWindowRegistration;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        StartupDiagnostics.Write("App.OnStartup entered.");
        base.OnStartup(e);
        StartupDiagnostics.Write("App.OnStartup after base.");

        var createdNew = false;

        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            StartupDiagnostics.Write("Another instance detected. Signalling restore and shutting down.");
            SignalExistingInstanceToRestore();
            Shutdown();
            return;
        }

        StartupDiagnostics.Write("Single-instance mutex acquired.");
        _restoreWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RestoreWindowEventName);
        _restoreWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _restoreWindowEvent,
            static (state, _) =>
            {
                if (state is not App app)
                {
                    return;
                }

                app.Dispatcher.BeginInvoke(() =>
                {
                    if (app.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.RestoreFromExternalLaunch();
                    }
                });
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);

        StartupDiagnostics.Write("Creating MainWindow.");
        var mainWindow = new MainWindow();
        StartupDiagnostics.Write("MainWindow constructed.");
        MainWindow = mainWindow;
        mainWindow.Show();
        StartupDiagnostics.Write("MainWindow shown.");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        StartupDiagnostics.Write("App.OnExit entered.");
        _restoreWindowRegistration?.Unregister(null);
        _restoreWindowRegistration = null;

        _restoreWindowEvent?.Dispose();
        _restoreWindowEvent = null;

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
        StartupDiagnostics.Write("App.OnExit completed.");
    }

    private static void SignalExistingInstanceToRestore()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var restoreEvent = EventWaitHandle.OpenExisting(RestoreWindowEventName);
                restoreEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
