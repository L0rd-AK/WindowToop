using System.Windows;
using System.Windows.Interop;
using Mullion.App.Interop;
using Mullion.Core;

namespace Mullion.App.UI;

/// <summary>
/// The floating mini-toolbar. Borderless, topmost, and WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW so clicking it
/// never steals focus from the target window and it never shows up in Alt-Tab. Positioned in physical
/// pixels through SetWindowPos (WPF's Left/Top are DIPs and get confusing across monitors).
/// </summary>
public partial class ToolbarWindow : Window
{
    private bool _syncing;

    public ToolbarWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            ApplyExStyles();
        };
    }

    public nint Handle { get; private set; }

    public event Action? PinToggled;

    public event Action<int>? OpacityChanged;

    public event Action? GhostToggled;

    public event Action? PipRequested;

    public event Action? ResetRequested;

    public bool PipAvailable
    {
        set => PipButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Current size in physical pixels (0x0 until the window has been created).</summary>
    public PixelRect ScreenRect => Handle == 0 ? default : Win32WindowOps.GetWindowRect(Handle);

    /// <summary>Reflects the target's state in the controls without raising change events.</summary>
    public void Bind(TrackedWindow? state, bool nativelyTopmost)
    {
        _syncing = true;
        try
        {
            PinButton.IsChecked = state?.IsTopmost ?? nativelyTopmost;
            OpacitySlider.Value = state?.Opacity ?? OpacityMath.Max;
            OpacityLabel.Text = $"{(int)OpacitySlider.Value}%";
            GhostButton.IsChecked = state?.ClickThrough ?? false;
            ResetButton.IsEnabled = state is not null;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Shows (without activating) and moves the toolbar so its top-left lands at the given physical pixel.</summary>
    public void ShowAt(int x, int y)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (Handle == 0)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Re-asserts the topmost z-order (after the target itself became topmost, for example).</summary>
    public void BringToTop()
    {
        if (Handle != 0 && IsVisible)
        {
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    public void HideToolbar()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void ApplyExStyles()
    {
        nint current = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);
        nint wanted = current | (nint)(WindowStyles.WS_EX_NOACTIVATE | WindowStyles.WS_EX_TOOLWINDOW);
        if (wanted != current)
        {
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, wanted);
        }
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            PinToggled?.Invoke();
        }
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires while XAML is still being parsed (Value="100"), before the label element exists.
        if (OpacityLabel is null)
        {
            return;
        }

        OpacityLabel.Text = $"{(int)e.NewValue}%";
        if (!_syncing && IsLoaded)
        {
            OpacityChanged?.Invoke((int)e.NewValue);
        }
    }

    private void OnGhost(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            GhostToggled?.Invoke();
        }
    }

    private void OnPip(object sender, RoutedEventArgs e) => PipRequested?.Invoke();

    private void OnReset(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();
}
