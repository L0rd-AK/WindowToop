using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Mullion.App.Interop;
using Mullion.Core;

namespace Mullion.App.Services;

internal enum NotifyLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Notification-area icon and its context menu. The menu is rebuilt every time it opens so the list of
/// modified windows is always current. The icon is drawn at runtime (no bundled artwork).
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly WindowController _controller;
    private readonly Func<AppSettings> _getSettings;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _drawnIcon;
    private readonly nint _iconHandle;
    private bool _disposed;

    public TrayService(WindowController controller, Func<AppSettings> getSettings)
    {
        _controller = controller;
        _getSettings = getSettings;

        (_drawnIcon, _iconHandle) = CreateIcon();
        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += (_, _) => RebuildMenu();

        _icon = new NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "Mullion",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public event Action? ExitRequested;

    public event Action? RestoreAllRequested;

    public event Action<bool>? ToolbarEnabledChanged;

    public event Action? SettingsRequested;

    public event Action? DebugConsoleRequested;

    public event Action<nint>? PipRequested;

    /// <summary>Set once the picture-in-picture feature is wired up (phase 5).</summary>
    public bool PipAvailable { get; set; }

    public void Notify(string title, string text, NotifyLevel level = NotifyLevel.Info)
    {
        if (_disposed)
        {
            return;
        }

        var icon = level switch
        {
            NotifyLevel.Warning => ToolTipIcon.Warning,
            NotifyLevel.Error => ToolTipIcon.Error,
            _ => ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(4000, title, text, icon);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _drawnIcon.Dispose();
        if (_iconHandle != 0)
        {
            NativeMethods.DestroyIcon(_iconHandle);
        }
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var settings = _getSettings();

        var header = new ToolStripMenuItem("Mullion") { Enabled = false, Font = new Font(_menu.Font, FontStyle.Bold) };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        _controller.Prune();
        var tracked = _controller.TrackedWindows;
        if (tracked.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("No modified windows") { Enabled = false });
        }
        else
        {
            foreach (var window in tracked.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                _menu.Items.Add(BuildWindowItem(window));
            }

            var restoreAll = new ToolStripMenuItem("Restore all windows");
            restoreAll.Click += (_, _) => RestoreAllRequested?.Invoke();
            _menu.Items.Add(restoreAll);
        }

        _menu.Items.Add(new ToolStripSeparator());

        var toolbar = new ToolStripMenuItem("Show floating toolbar on title bars") { CheckOnClick = true, Checked = settings.ToolbarEnabled };
        toolbar.CheckedChanged += (_, _) => ToolbarEnabledChanged?.Invoke(toolbar.Checked);
        _menu.Items.Add(toolbar);

        var hotkeys = new ToolStripMenuItem("Settings and hotkeys...");
        hotkeys.Click += (_, _) => SettingsRequested?.Invoke();
        _menu.Items.Add(hotkeys);

        var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartupService.IsEnabled() };
        startup.CheckedChanged += (_, _) =>
        {
            string? error = StartupService.TrySetEnabled(startup.Checked);
            if (error is not null)
            {
                Notify("Start with Windows", error, NotifyLevel.Error);
            }
        };
        _menu.Items.Add(startup);

        var debug = new ToolStripMenuItem("Debug console...");
        debug.Click += (_, _) => DebugConsoleRequested?.Invoke();
        _menu.Items.Add(debug);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit (restores all windows)");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exit);
    }

    private ToolStripMenuItem BuildWindowItem(TrackedWindow window)
    {
        string title = window.Title.Length > 48 ? window.Title[..45] + "..." : window.Title;
        var item = new ToolStripMenuItem(window.ToString().Replace(window.Title, title, StringComparison.Ordinal));
        nint hwnd = window.Hwnd;

        var topmost = new ToolStripMenuItem("Always on top") { Checked = window.IsTopmost, CheckOnClick = true };
        topmost.Click += (_, _) => _controller.SetTopmost(hwnd, topmost.Checked);
        item.DropDownItems.Add(topmost);

        var clickThrough = new ToolStripMenuItem("Click-through") { Checked = window.ClickThrough, CheckOnClick = true };
        clickThrough.Click += (_, _) => _controller.SetClickThrough(hwnd, clickThrough.Checked);
        item.DropDownItems.Add(clickThrough);

        var opacity = new ToolStripMenuItem($"Opacity: {window.Opacity}%");
        for (int percent = OpacityMath.Max; percent >= OpacityMath.Min; percent -= 10)
        {
            int value = percent;
            var step = new ToolStripMenuItem($"{value}%") { Checked = window.Opacity == value };
            step.Click += (_, _) => _controller.SetOpacity(hwnd, value);
            opacity.DropDownItems.Add(step);
        }

        item.DropDownItems.Add(opacity);

        if (PipAvailable)
        {
            var pip = new ToolStripMenuItem("Picture-in-picture");
            pip.Click += (_, _) => PipRequested?.Invoke(hwnd);
            item.DropDownItems.Add(pip);
        }

        item.DropDownItems.Add(new ToolStripSeparator());
        var restore = new ToolStripMenuItem("Restore original state");
        restore.Click += (_, _) => _controller.Restore(hwnd);
        item.DropDownItems.Add(restore);

        return item;
    }

    /// <summary>Draws a rounded tile with a vertical bar through two panes: a mullion. 32x32 so it scales down cleanly.</summary>
    private static (Icon Icon, nint Handle) CreateIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var tile = new SolidBrush(Color.FromArgb(255, 38, 42, 51));
            using var tilePath = RoundedRect(new RectangleF(1, 1, size - 2, size - 2), 7);
            g.FillPath(tile, tilePath);

            using var pane = new SolidBrush(Color.FromArgb(255, 91, 156, 255));
            g.FillRectangle(pane, 7, 7, 7, 18);
            g.FillRectangle(pane, 18, 7, 7, 18);

            using var bar = new SolidBrush(Color.FromArgb(255, 232, 234, 240));
            g.FillRectangle(bar, 14.5f, 5, 3, 22);
        }

        nint handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
