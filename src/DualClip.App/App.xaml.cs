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
        base.OnStartup(e);

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
            SignalExistingInstanceToRestore();
            Shutdown();
            return;
        }

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

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
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
