using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace NetWatcher.App;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _isExitRequested;
    private bool _mainWindowClosed;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            try
            {
                CreateAndShowMainWindow(desktop);
                InitializeTrayIcon();
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateAndShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _mainWindowClosed = false;
        _mainWindow = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
            Icon = AppIconFactory.CreateWindowIcon(),
            ShowInTaskbar = true,
            WindowState = WindowState.Normal
        };

        _mainWindow.Closing += MainWindowOnClosing;
        _mainWindow.Closed += MainWindowOnClosed;
        desktop.MainWindow = _mainWindow;

        // Ensure visible without relying on tray re-show after close.
        Dispatcher.UIThread.Post(() =>
        {
            if (_isExitRequested || _mainWindow is null || _mainWindowClosed)
            {
                return;
            }

            try
            {
                _mainWindow.ShowInTaskbar = true;
                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }

                if (!_mainWindow.IsVisible)
                {
                    _mainWindow.Show();
                }

                _mainWindow.Activate();
            }
            catch (InvalidOperationException ex)
            {
                // Window already closing/closed — ignore.
                WriteCrashLog(ex);
            }
        }, DispatcherPriority.Loaded);
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var showItem = new NativeMenuItem("顯示主視窗");
            showItem.Click += OnTrayShowClicked;

            var exitItem = new NativeMenuItem("結束程式");
            exitItem.Click += OnTrayExitClicked;

            var menu = new NativeMenu();
            menu.Add(showItem);
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = AppIconFactory.CreateWindowIcon(),
                ToolTipText = "NetWatcher 網路速度監控器",
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += OnTrayIconClicked;
        }
        catch
        {
            _trayIcon = null;
        }
    }

    private void OnTrayShowClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnTrayExitClicked(object? sender, EventArgs e) => ExitApplication();

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void MainWindowOnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        // Mark exit early and drop tray first so click handlers cannot re-Show a closing window.
        _isExitRequested = true;
        DisposeTray();
        DisposeServices();
    }

    private void MainWindowOnClosed(object? sender, EventArgs e)
    {
        _mainWindowClosed = true;
        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= MainWindowOnClosing;
            _mainWindow.Closed -= MainWindowOnClosed;
        }

        _mainWindow = null;
    }

    private void ShowMainWindow()
    {
        if (_isExitRequested)
        {
            return;
        }

        try
        {
            // Closed windows cannot be shown again in Avalonia — recreate if needed.
            if (_mainWindow is null || _mainWindowClosed)
            {
                if (_desktop is null)
                {
                    return;
                }

                _isExitRequested = false;
                CreateAndShowMainWindow(_desktop);
                return;
            }

            _mainWindow.ShowInTaskbar = true;
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            _mainWindow.Activate();
        }
        catch (InvalidOperationException ex)
        {
            WriteCrashLog(ex);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
        }
    }

    private void ExitApplication()
    {
        if (_isExitRequested && _mainWindow is null)
        {
            _desktop?.Shutdown();
            return;
        }

        _isExitRequested = true;
        DisposeTray();
        DisposeServices();

        try
        {
            _mainWindow?.Close();
        }
        catch
        {
            // Ignore close races.
        }

        _desktop?.Shutdown();
    }

    private void DisposeServices()
    {
        if (_mainWindow?.DataContext is not IDisposable disposable)
        {
            return;
        }

        // Detach first so Closing/Closed handlers cannot touch a disposing VM.
        if (_mainWindow is not null)
        {
            _mainWindow.DataContext = null;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Ignore dispose failures on exit.
        }
    }

    private void DisposeTray()
    {
        if (_trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.Clicked -= OnTrayIconClicked;
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        }
        catch
        {
            // Ignore tray cleanup failures.
        }

        _trayIcon = null;
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
