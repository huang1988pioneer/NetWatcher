using Avalonia;
using System.Diagnostics;

namespace NetWatcher.App;

internal sealed class Program
{
    private const string MutexName = @"Local\NetWatcher.App.SingleInstance.v1";

    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance: prefer one process. If the mutex is held by a dead/orphaned owner,
        // still allow start so users are not stuck with "系統資源不足" / silent fail.
        if (!TryAcquireSingleInstance(out var mutex))
        {
            if (HasResponsiveInstance())
            {
                return;
            }

            // Stale mutex / zombie: try again after abandoning wait.
            try
            {
                mutex?.Dispose();
            }
            catch
            {
                // ignore
            }

            if (!TryAcquireSingleInstance(out mutex))
            {
                // Last resort: continue without exclusive mutex rather than blocking forever.
                mutex = null;
            }
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
            try
            {
                mutex?.ReleaseMutex();
            }
            catch
            {
                // ignore
            }

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
        try
        {
            mutex = new Mutex(true, MutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }

            // Another process owns it — check if it is still alive.
            try
            {
                // Wait briefly; abandoned mutex means previous owner crashed.
                var owned = mutex.WaitOne(0);
                if (owned)
                {
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                return true;
            }

            mutex.Dispose();
            mutex = null;
            return false;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            mutex = null;
            // Mutex creation failure (resource pressure / permissions): allow app to start.
            return true;
        }
    }

    private static bool HasResponsiveInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                try
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    if (!process.HasExited && process.Responding)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Protected process metadata.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Ignore enumeration failures.
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
