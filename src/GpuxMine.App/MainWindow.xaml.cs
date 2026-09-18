using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GpuxMine.App.ViewModels;
using GpuxMine.App.Views;

namespace GpuxMine.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _screens;

    public MainWindow()
    {
        InitializeComponent();

        // One instance of each screen, kept alive so a page's scroll position
        // and half-typed values survive tabbing away. Only the visible one is
        // in the tree, which keeps binding traffic to what is on screen.
        _screens = new Dictionary<string, UserControl>(StringComparer.Ordinal)
        {
            ["dashboard"] = new DashboardView(),
            ["power"] = new PowerView(),
            ["models"] = new ModelsView(),
            ["queue"] = new QueueView(),
            ["wallet"] = new WalletView(),
            ["benchmark"] = new BenchmarkView(),
            ["log"] = new LogView(),
            ["referrals"] = new ReferralsView(),
            ["settings"] = new SettingsView(),
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CurrentScreen)) ShowScreen(vm.CurrentScreen); };
                ShowScreen(vm.CurrentScreen);
            }
        };
    }

    private void ShowScreen(string key)
    {
        if (!_screens.TryGetValue(key, out var view)) view = _screens["dashboard"];
        ScreenHost.Content = view;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>
    /// Closing the window hides it; the node keeps earning in the tray.
    /// </summary>
    /// <remarks>
    /// The owner closed a window. They did not decide to stop sharing their
    /// card, and a worker that stops the moment its window is dismissed is a
    /// worker that quietly earns nothing. Quitting for real is on the tray menu.
    /// </remarks>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
