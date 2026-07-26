using Avalonia;
using System.Diagnostics;

namespace NetWatcher.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance: bring existing window forward instead of a second silent process.
        if (!TryAcquireSingleInstance(out var mutex) && ActivateExistingInstance())
        {
            return;
        }

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    WriteCrashLog(ex);
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteCrashLog(e.Exception);
                e.SetObserved();
            };

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static bool TryAcquireSingleInstance(out Mutex? mutex)
    {
        mutex = new Mutex(true, @"Local\NetWatcher.App.SingleInstance", out var createdNew);
        if (createdNew)
        {
            return true;
        }

        mutex.Dispose();
        mutex = null;
        return false;
    }

    private static bool ActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

                // Best-effort: existing instance keeps its tray/window; user can Alt+Tab.
                return true;
            }
        }
        catch
        {
            // Ignore activation failures; continue launching.
        }

        return false;
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-crash.log");
            File.AppendAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}

