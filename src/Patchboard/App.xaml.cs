using System.Windows;
using System.Windows.Threading;

namespace Patchboard;

/// <summary>
/// Application entry point, and the last line of defence for an unhandled exception.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Without this, any unhandled exception on the UI thread kills the process on the
        // spot: the window vanishes mid-game with a Windows crash dialog, and whatever was
        // not yet saved is gone. A soundboard is not worth losing a library over, and most
        // of what can throw here is recoverable, a device pulled out mid-call being the
        // obvious one. Report it and carry on.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // A background thread cannot be rescued, but it can at least say what happened
        // rather than closing in silence. WASAPI callbacks run on their own threads.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrashLog(ex);
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);

        MessageBox.Show(
            $"Patchboard hit an error and carried on:\n\n{e.Exception.Message}\n\n" +
            $"Details were written to:\n{Services.ConfigService.ConfigDirectory}",
            "Patchboard",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Keep running. The alternative is closing the app over something that is usually
        // one dead device rather than a broken program.
        e.Handled = true;
    }

    /// <summary>
    /// Append a crash to a log beside the config. Best effort: the handler must not throw,
    /// because an exception escaping here is the one that really does end the process.
    /// </summary>
    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Services.ConfigService.ConfigDirectory);
            var path = Path.Combine(Services.ConfigService.ConfigDirectory, "errors.log");

            // Truncate rather than grow without limit. A repeating fault would otherwise
            // fill the disk one stack trace at a time.
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);

            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
