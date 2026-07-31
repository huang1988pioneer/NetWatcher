using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NetWatcher.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ClearAllLimitsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ClearAllLimitsAsync();
        }
    }

    private void RestartAsAdminButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.TryRestartAsAdministrator();
        }
    }
}
