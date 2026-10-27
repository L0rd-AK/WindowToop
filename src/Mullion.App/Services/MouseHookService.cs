using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mullion.App.Interop;

namespace Mullion.App.Services;

/// <summary>
/// WH_MOUSE_LL hook used only to learn where the pointer is. The callback stores the last position and
/// posts (at most one pending) dispatcher operation; all real work happens in <see cref="MouseMoved"/>.
/// Low-level hooks have a system-enforced timeout, so the callback must stay trivial.
/// </summary>
internal sealed class MouseHookService : IDisposable
{
    // Static so the native thunk can never be garbage-collected while the hook is installed.
    private static readonly NativeMethods.LowLevelMouseProc Callback = HookProc;
    private static MouseHookService? s_instance;

    private readonly Dispatcher _dispatcher;
    private readonly Action _drain;
    private readonly nint _hook;
    private int _pending;
    private int _x;
    private int _y;
    private bool _disposed;

    public MouseHookService(Dispatcher dispatcher)
    {
        if (s_instance is not null)
        {
            throw new InvalidOperationException("Only one MouseHookService may exist per process.");
        }

        _dispatcher = dispatcher;
        _drain = Drain;
        s_instance = this;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, Callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            Diagnostics.Log($"SetWindowsHookEx(WH_MOUSE_LL) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>Raised on the dispatcher thread with the latest pointer position in physical screen pixels.</summary>
    public event Action<int, int>? MouseMoved;

    public bool IsInstalled => _hook != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
        }

        s_instance = null;
    }

    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        if (code >= NativeMethods.HC_ACTION && wParam == NativeMethods.WM_MOUSEMOVE && s_instance is { _disposed: false } service)
        {
            // MSLLHOOKSTRUCT starts with POINT { int x; int y; }.
            service.Enqueue(Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, sizeof(int)));
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private void Enqueue(int x, int y)
    {
        Volatile.Write(ref _x, x);
        Volatile.Write(ref _y, y);
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Input, _drain);
        }
    }

    private void Drain()
    {
        Volatile.Write(ref _pending, 0);
        if (_disposed)
        {
            return;
        }

        try
        {
            MouseMoved?.Invoke(Volatile.Read(ref _x), Volatile.Read(ref _y));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Mouse move handler failed: " + ex);
        }
    }
}
