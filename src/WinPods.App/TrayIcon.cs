using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WinPods.App.ViewModels;
using Forms = System.Windows.Forms;

namespace WinPods.App;

/// <summary>Notification-area icon showing the lowest earbud battery, with quick actions.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pullHere;
    private readonly Forms.ToolStripMenuItem _pushToPeer;
    private readonly Forms.ToolStripMenuItem[] _modes;
    private Icon? _currentIcon;

    public TrayIcon(MainViewModel vm, Action open, Action exit)
    {
        _vm = vm;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir WinPods", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());

        _modes =
        [
            ModeItem("Cancelación de ruido", "NoiseCancellation"),
            ModeItem("Adaptativo", "Adaptive"),
            ModeItem("Transparencia", "Transparency"),
            ModeItem("Desactivado", "Off"),
        ];
        menu.Items.AddRange(_modes);
        menu.Items.Add(new Forms.ToolStripSeparator());

        _pullHere = new Forms.ToolStripMenuItem("Traer audio a este PC", null, (_, _) => vm.PullHereCommand.Execute(null));
        _pushToPeer = new Forms.ToolStripMenuItem("Enviar audio al Mac", null, (_, _) => vm.PushToPeerCommand.Execute(null));
        menu.Items.Add(_pullHere);
        menu.Items.Add(_pushToPeer);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => exit());
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };

        vm.BatteryChanged += Refresh;
        vm.PropertyChanged += OnViewModelChanged;
        Refresh();
    }

    private Forms.ToolStripMenuItem ModeItem(string text, string mode) =>
        new(text, null, (_, _) => _vm.SetNoiseModeCommand.Execute(mode)) { Tag = mode };

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DeviceName) or nameof(MainViewModel.IsConnected)) Refresh();
    }

    private void RefreshMenu()
    {
        foreach (var item in _modes)
        {
            item.Enabled = _vm.AapConnected;
            item.Checked = _vm.NoiseMode?.ToString() == (string)item.Tag!;
        }
        _pullHere.Enabled = _pushToPeer.Enabled = _vm.CanHandoff;
    }

    private void Refresh()
    {
        _icon.Text = _vm.TraySummary;
        var old = _currentIcon;
        _currentIcon = DrawIcon(_vm.LowestBudBattery, _vm.IsConnected);
        _icon.Icon = _currentIcon;
        if (old is not null) { DestroyIcon(old.Handle); old.Dispose(); }
    }

    /// <summary>Draws a small rounded badge with the battery percentage.</summary>
    private static Icon DrawIcon(int? percent, bool connected)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color fill = percent switch
            {
                null => Color.FromArgb(120, 120, 120),
                <= 20 => Color.FromArgb(255, 69, 58),
                _ => connected ? Color.FromArgb(10, 132, 255) : Color.FromArgb(90, 90, 90),
            };
            using var path = new GraphicsPath();
            path.AddArc(0, 0, 12, 12, 180, 90);
            path.AddArc(size - 13, 0, 12, 12, 270, 90);
            path.AddArc(size - 13, size - 13, 12, 12, 0, 90);
            path.AddArc(0, size - 13, 12, 12, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);

            string text = percent is int p ? (p >= 100 ? "99" : p.ToString()) : "–";
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, size, size), format);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _vm.BatteryChanged -= Refresh;
        _vm.PropertyChanged -= OnViewModelChanged;
        _icon.Visible = false;
        _icon.Dispose();
        if (_currentIcon is not null) { DestroyIcon(_currentIcon.Handle); _currentIcon.Dispose(); }
    }
}
