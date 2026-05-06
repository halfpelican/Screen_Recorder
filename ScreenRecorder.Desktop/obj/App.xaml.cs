using System.Windows;
using System.Windows.Threading;

namespace ScreenRecorder.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Register handlers BEFORE base.OnStartup so any exception thrown
        // during MainWindow initialisation (e.g. XamlParseException) is caught
        // and shown as a dialog rather than silently killing the process.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowError("Unhandled UI Error", e.Exception);
        e.Handled = true; // Keep the process alive so the user sees the dialog.
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var message = e.ExceptionObject is Exception ex
            ? $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}"
            : e.ExceptionObject?.ToString() ?? "Unknown error";

        MessageBox.Show(
            $"A fatal background error occurred:\n\n{message}",
            "Fatal Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved(); // Prevent process termination.
        ShowError("Unobserved Task Error", e.Exception);
    }

    private static void ShowError(string title, Exception? ex)
    {
        var message = ex is null
            ? "An unknown error occurred."
            : $"{ex.GetType().Name}: {ex.Message}" +
              (ex.InnerException is not null ? $"\n\nCaused by: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : string.Empty) +
              $"\n\n{ex.StackTrace}";

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
