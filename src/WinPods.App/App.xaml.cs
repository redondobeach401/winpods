using System.Windows;
using WinPods.App.Services;
using WinPods.App.ViewModels;

namespace WinPods.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private Mutex? _singleInstance;

    public static bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\WinPods.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("WinPods ya se está ejecutando (mira la bandeja del sistema).", "WinPods");
            Shutdown();
            return;
        }

        _viewModel = new MainViewModel(AppSettings.Load(), Dispatcher);
        _tray = new TrayIcon(_viewModel, ShowWindow, ExitApp);

        if (!e.Args.Contains("--tray")) ShowWindow();
        await _viewModel.InitializeAsync();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void ExitApp()
    {
        IsExiting = true;
        _tray?.Dispose();
        if (_viewModel is not null) await _viewModel.DisposeAsync();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }
}
