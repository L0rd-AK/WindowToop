using Mullion.App.Interop;
using Mullion.Core;

namespace Mullion.App.Services;

/// <summary>
/// Hides a picture-in-picture source so its app keeps rendering while covered (see <see cref="PipKeepAlive"/>),
/// and puts it back exactly as it was. One instance per source window, owned by its PiP frame.
/// </summary>
internal sealed class SourceKeepAlive
{
    private readonly Win32WindowOps _ops;
    private readonly uint _originalExStyle;
    private readonly byte? _originalAlpha;
    private readonly bool _wasMinimized;
    private bool _restored;

    private SourceKeepAlive(Win32WindowOps ops, nint hwnd, uint originalExStyle, byte? originalAlpha, bool wasMinimized)
    {
        _ops = ops;
        Hwnd = hwnd;
        _originalExStyle = originalExStyle;
        _originalAlpha = originalAlpha;
        _wasMinimized = wasMinimized;
    }

    public nint Hwnd { get; }

    /// <summary>Applies keep-alive. Returns null and a user-facing reason when the window can't be hidden this way.</summary>
    public static SourceKeepAlive? TryApply(Win32WindowOps ops, nint hwnd, byte ghostAlpha, out string? problem)
    {
        problem = null;
        uint exStyle = ops.GetExStyle(hwnd);
        uint? layeredFlags = null;
        byte? currentAlpha = null;
        if ((exStyle & WindowStyles.WS_EX_LAYERED) != 0
            && NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags))
        {
            layeredFlags = flags;
            currentAlpha = (flags & NativeMethods.LWA_ALPHA) != 0 ? alpha : byte.MaxValue;
        }

        if (!PipKeepAlive.CanGhost(exStyle, layeredFlags))
        {
            problem = "This app draws its own transparent window, so its video may pause while other windows cover it.";
            return null;
        }

        // A minimized window is not rendered at all, so the thumbnail would stay frozen. Bring it back
        // (without activating it); it is hidden by the ghost style anyway and re-minimized on close.
        bool wasMinimized = Win32WindowOps.IsMinimized(hwnd);
        if (wasMinimized)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        var keepAlive = new SourceKeepAlive(ops, hwnd, exStyle, currentAlpha, wasMinimized);

        bool ok = ops.SetExStyle(hwnd, PipKeepAlive.GhostExStyle(exStyle))
            && ops.SetLayeredAlpha(hwnd, ghostAlpha)
            && ops.SetTopmost(hwnd, true);
        if (!ok)
        {
            int error = ops.LastError;
            keepAlive.Restore();
            problem = error switch
            {
                IWindowOps.ErrorAccessDenied => "This window belongs to an elevated app, so Mullion can't keep it rendering while covered. Run Mullion as administrator for that.",
                IWindowOps.ErrorStyleRejected => "This app manages its own window style, so its video may pause while other windows cover it.",
                _ => $"The window could not be kept awake (Win32 error {error}); its video may pause while covered.",
            };
            return null;
        }

        return keepAlive;
    }

    /// <summary>Undoes everything. Safe to call more than once and after the window is gone.</summary>
    public void Restore()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        if (!_ops.IsWindow(Hwnd))
        {
            return;
        }

        try
        {
            uint current = _ops.GetExStyle(Hwnd);
            uint target = PipKeepAlive.RestoreExStyle(current, _originalExStyle);
            if (target != current)
            {
                _ops.SetExStyle(Hwnd, target);
            }

            if (_originalAlpha is { } alpha && (target & WindowStyles.WS_EX_LAYERED) != 0)
            {
                _ops.SetLayeredAlpha(Hwnd, alpha);
            }

            if ((_originalExStyle & WindowStyles.WS_EX_TOPMOST) == 0)
            {
                _ops.SetTopmost(Hwnd, false);
            }

            _ops.Redraw(Hwnd);

            if (_wasMinimized)
            {
                NativeMethods.ShowWindow(Hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Keep-alive restore failed: " + ex);
        }
    }
}
