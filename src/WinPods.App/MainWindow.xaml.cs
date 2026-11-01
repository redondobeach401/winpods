using System.ComponentModel;
using System.Windows;

namespace WinPods.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Closing the window keeps WinPods running in the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.IsExiting) return;
        e.Cancel = true;
        Hide();
    }
}
