using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace NetWatcher.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        HighlightNav(AppNavPage.Overview);
    }

    private async void ExportCsvButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ExportTrafficHistoryAsync();
        }
    }

    private async void ClearAllLimitsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ClearAllLimitsAsync();
        }
    }

    private void RefreshInterfacesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RefreshNetworkInterfaces();
        }
    }

    private void RestartAsAdminButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.TryRestartAsAdministrator();
        }
    }

    private void RetryEtwButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RetryEtwMonitoring();
        }
    }

    private void SettingsNavButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo(AppNavPage.Settings);
    }

    private void NavButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<AppNavPage>(tag, ignoreCase: true, out var page))
        {
            return;
        }

        NavigateTo(page);
    }

    private void NavigateTo(AppNavPage page)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectNav(page);
        }

        HighlightNav(page);
    }

    private void HighlightNav(AppNavPage page)
    {
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            if (button.Classes.Contains("ui-nav") && button.Tag is string tag)
            {
                var active = Enum.TryParse<AppNavPage>(tag, true, out var nav) && nav == page;
                if (active)
                {
                    if (!button.Classes.Contains("active"))
                    {
                        button.Classes.Add("active");
                    }
                }
                else
                {
                    button.Classes.Remove("active");
                }
            }
        }
    }
}
