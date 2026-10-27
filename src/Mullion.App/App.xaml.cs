using System.Windows;
using System.Windows.Threading;
using Mullion.App.Services;
using Mullion.Core;

namespace Mullion.App;

/// <summary>
/// Application entry point. Owns the single-instance mutex and makes sure <see cref="AppHost"/> is torn down
/// (hooks removed, every modified window restored) on every exit path: normal exit, dispatcher exceptions,
/// unhandled exceptions on other threads, and ProcessExit.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Mullion.SingleInstance.5c1f0f6e";

    private Mutex? _instanceMutex;
    private AppHost? _host;
    private int _tornDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = LaunchOptions.Parse(e.Args);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew && !options.SelfTest)
        {
            MessageBox.Show(
                "Mullion is already running. Look for its icon in the notification area.",
                "Mullion",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Teardown();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Diagnostics.Log("Unhandled exception: " + args.ExceptionObject);
            Teardown();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diagnostics.Log("Unobserved task exception: " + args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _host = new AppHost(options);
        try
        {
            _host.Start();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Startup failed: " + ex);
            MessageBox.Show("Mullion could not start:\n\n" + ex.Message, "Mullion", MessageBoxButton.OK, MessageBoxImage.Error);
            Teardown();
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.Log("Dispatcher exception: " + e.Exception);

        // Put every window back before we show anything or go down.
        Teardown();
        e.Handled = true;
        MessageBox.Show(
            "Mullion hit an unexpected error and will close. Modified windows have been restored.\n\n" + e.Exception.Message,
            "Mullion",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Teardown();
        base.OnExit(e);
    }

    private void Teardown()
    {
        if (Interlocked.Exchange(ref _tornDown, 1) != 0)
        {
            return;
        }

        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Teardown failed: " + ex);
        }

        try
        {
            _instanceMutex?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }
    }
}
