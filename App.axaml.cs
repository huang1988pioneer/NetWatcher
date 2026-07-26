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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            // Close main window = exit app (avoids "flash then disappear to tray" confusion).
            _desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            try
            {
                _mainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                    Icon = AppIconFactory.CreateWindowIcon(),
                    ShowInTaskbar = true,
                    WindowState = WindowState.Normal
                };

                _mainWindow.Closing += MainWindowOnClosing;
                desktop.MainWindow = _mainWindow;
                InitializeTrayIcon();

                // Defer activate to next UI tick so the window stays visible after layout.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_mainWindow is null || _isExitRequested)
                    {
                        return;
                    }

                    _mainWindow.ShowInTaskbar = true;
                    if (_mainWindow.WindowState == WindowState.Minimized)
                    {
                        _mainWindow.WindowState = WindowState.Normal;
                    }

                    _mainWindow.Show();
                    _mainWindow.Activate();
                }, DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var showItem = new NativeMenuItem("顯示主視窗");
            showItem.Click += (_, _) => ShowMainWindow();

            var exitItem = new NativeMenuItem("結束程式");
            exitItem.Click += (_, _) => ExitApplication();

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
            _trayIcon.Clicked += (_, _) => ShowMainWindow();
        }
        catch
        {
            // Tray is optional; never block startup if icon fails.
            _trayIcon = null;
        }
    }

    private void MainWindowOnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        // Normal close: tear down services then exit.
        _isExitRequested = true;
        DisposeServices();
        DisposeTray();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowInTaskbar = true;
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        if (_isExitRequested)
        {
            return;
        }

        _isExitRequested = true;
        DisposeServices();
        DisposeTray();
        _mainWindow?.Close();
        _desktop?.Shutdown();
    }

    private void DisposeServices()
    {
        if (_mainWindow?.DataContext is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Ignore dispose failures on exit.
            }
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
            File.WriteAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{ex}");
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
