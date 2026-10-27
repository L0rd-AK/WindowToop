using System.Runtime.InteropServices;
using System.Text;
using Mullion.Core;

namespace Mullion.App.Interop;

/// <summary>
/// <see cref="IWindowOps"/> implemented over <see cref="NativeMethods"/>, plus a few static helpers
/// (geometry, DPI, enumeration) the UI layer needs but the core logic does not.
/// </summary>
internal sealed class Win32WindowOps : IWindowOps
{
    public int LastError { get; private set; }

    public bool IsWindow(nint hwnd) => hwnd != 0 && NativeMethods.IsWindow(hwnd);

    public nint GetRootWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        nint root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        return root == 0 ? hwnd : root;
    }

    public WindowSnapshot? TryGetSnapshot(nint hwnd)
    {
        if (!IsWindow(hwnd))
        {
            return null;
        }

        nint root = GetRootWindow(hwnd);
        bool visible = NativeMethods.IsWindowVisible(hwnd) && !IsCloaked(hwnd);
        uint exStyle = GetExStyle(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        return new WindowSnapshot(hwnd, root, visible, exStyle, GetWindowText(hwnd), GetClassName(hwnd), processId);
    }

    public uint GetExStyle(nint hwnd) => unchecked((uint)(long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE));

    public bool SetExStyle(nint hwnd, uint exStyle)
    {
        // SetWindowLongPtr returns the previous value, and 0 is legal, so failure is only detectable
        // by clearing the thread error first and checking it afterwards.
        Marshal.SetLastSystemError(0);
        nint previous = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, unchecked((nint)(int)exStyle));
        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0)
        {
            LastError = error;
            return false;
        }

        // The call can succeed while the window's WM_STYLECHANGING handler quietly drops bits (WPF does
        // this with WS_EX_LAYERED). Read back and report that as a rejection rather than a success.
        uint applied = GetExStyle(hwnd);
        if ((applied & WindowStyles.ManagedBits) != (exStyle & WindowStyles.ManagedBits))
        {
            LastError = IWindowOps.ErrorStyleRejected;
            return false;
        }

        LastError = 0;
        return true;
    }

    public bool SetTopmost(nint hwnd, bool topmost)
    {
        if (!Check(SetZOrderBand(hwnd, topmost)))
        {
            return false;
        }

        // SetWindowPos can report success without changing the band (seen on WPF windows that were never
        // topmost before). Flipping to the opposite band first and back makes the request take.
        if (IsTopmostBitSet(hwnd) != topmost)
        {
            SetZOrderBand(hwnd, !topmost);
            if (!Check(SetZOrderBand(hwnd, topmost)))
            {
                return false;
            }

            if (IsTopmostBitSet(hwnd) != topmost)
            {
                LastError = IWindowOps.ErrorStyleRejected;
                return false;
            }
        }

        return true;
    }

    private bool IsTopmostBitSet(nint hwnd) => (GetExStyle(hwnd) & WindowStyles.WS_EX_TOPMOST) != 0;

    private static bool SetZOrderBand(nint hwnd, bool topmost) => NativeMethods.SetWindowPos(
        hwnd,
        topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
        0, 0, 0, 0,
        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    public bool SetLayeredAlpha(nint hwnd, byte alpha) =>
        Check(NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA));

    public void Redraw(nint hwnd) => NativeMethods.RedrawWindow(
        hwnd, 0, 0,
        NativeMethods.RDW_ERASE | NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_FRAME | NativeMethods.RDW_ALLCHILDREN);

    private bool Check(bool ok)
    {
        LastError = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    // ------------------------------------------------------------------ static helpers for the UI layer

    public static string GetWindowText(nint hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        int copied = NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString() : string.Empty;
    }

    public static string GetClassName(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        int copied = NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>UWP apps leave invisible "cloaked" frames behind; DWM reports them so we can skip them.</summary>
    public static bool IsCloaked(nint hwnd)
    {
        int hr = NativeMethods.DwmGetWindowAttributeBool(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    /// <summary>Byte alpha currently applied with LWA_ALPHA, or null if the window is not layered that way.</summary>
    public static byte? TryGetLayeredAlpha(nint hwnd)
    {
        if (NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) && (flags & NativeMethods.LWA_ALPHA) != 0)
        {
            return alpha;
        }

        return null;
    }

    /// <summary>Visible frame in physical pixels: DWM extended bounds (no invisible resize borders), else GetWindowRect.</summary>
    public static PixelRect GetFrameBounds(nint hwnd)
    {
        int hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<NativeMethods.RECT>());
        if (hr != 0 && !NativeMethods.GetWindowRect(hwnd, out rect))
        {
            return default;
        }

        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static PixelRect GetWindowRect(nint hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var rect) ? new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom) : default;

    public static PixelRect GetClientSize(nint hwnd) =>
        NativeMethods.GetClientRect(hwnd, out var rect) ? new PixelRect(0, 0, rect.Right, rect.Bottom) : default;

    public static uint GetDpi(nint hwnd)
    {
        uint dpi = hwnd == 0 ? 0 : NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : dpi;
    }

    /// <summary>Height of the caption area at the window's DPI, including the frame, in physical pixels.</summary>
    public static int GetCaptionHeight(nint hwnd)
    {
        uint dpi = GetDpi(hwnd);
        int caption = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYCAPTION, dpi);
        int frame = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYSIZEFRAME, dpi);
        int padded = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpi);
        int total = caption + frame + padded;
        return total > 0 ? total : (int)(32 * dpi / 96.0);
    }

    public static PixelRect GetWorkArea(nint hwnd) => GetWorkAreaOfMonitor(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));

    public static PixelRect GetWorkAreaAt(int x, int y) =>
        GetWorkAreaOfMonitor(NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST));

    private static PixelRect GetWorkAreaOfMonitor(nint monitor)
    {
        if (monitor == 0)
        {
            return default;
        }

        var info = new NativeMethods.MONITORINFO { Size = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return default;
        }

        return new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    public static (int X, int Y) GetCursorPosition()
    {
        return NativeMethods.GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);
    }

    /// <summary>All top-level window handles in Z order (front to back).</summary>
    public static List<nint> EnumerateTopLevelWindows()
    {
        var handles = new List<nint>();
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                handles.Add(hwnd);
                return true;
            },
            0);
        return handles;
    }

    public static bool IsMinimized(nint hwnd) => NativeMethods.IsIconic(hwnd);

    public static void RestoreAndActivate(nint hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(hwnd);
    }
}
