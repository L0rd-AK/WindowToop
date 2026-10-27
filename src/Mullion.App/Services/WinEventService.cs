using System.Runtime.InteropServices;
using Mullion.App.Interop;

namespace Mullion.App.Services;

/// <summary>
/// Out-of-context WinEvent hooks for foreground, location, minimize and destroy events. Because the hooks
/// are WINEVENT_OUTOFCONTEXT and installed on the UI thread, Windows delivers the callback through our
/// message loop, i.e. already on the UI thread. The callback does nothing but filter and raise events.
/// </summary>
internal sealed class WinEventService : IDisposable
{
    // Static so the native thunk can never be garbage-collected while a hook is installed.
    private static readonly NativeMethods.WinEventProc Callback = OnWinEvent;
    private static WinEventService? s_instance;

    private readonly List<nint> _hooks = new();
    private bool _disposed;

    private readonly uint _flags;

    /// <param name="includeOwnProcess">
    /// Normally our own windows (toolbar, PiP frames) are skipped with WINEVENT_SKIPOWNPROCESS.
    /// The self-test passes true because its only targets live in this process.
    /// </param>
    public WinEventService(bool includeOwnProcess = false)
    {
        if (s_instance is not null)
        {
            throw new InvalidOperationException("Only one WinEventService may exist per process.");
        }

        _flags = NativeMethods.WINEVENT_OUTOFCONTEXT | (includeOwnProcess ? 0 : NativeMethods.WINEVENT_SKIPOWNPROCESS);
        s_instance = this;
        Install(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        Install(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
        Install(NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_DESTROY);
        Install(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE);
    }

    public event Action<nint>? ForegroundChanged;

    public event Action<nint>? LocationChanged;

    public event Action<nint>? MinimizeStarted;

    public event Action<nint>? MinimizeEnded;

    public event Action<nint>? WindowDestroyed;

    public int HookCount => _hooks.Count;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (nint hook in _hooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }

        _hooks.Clear();
        s_instance = null;
    }

    private void Install(uint eventMin, uint eventMax)
    {
        nint hook = NativeMethods.SetWinEventHook(eventMin, eventMax, 0, Callback, 0, 0, _flags);
        if (hook == 0)
        {
            Diagnostics.Log($"SetWinEventHook({eventMin:X}-{eventMax:X}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return;
        }

        _hooks.Add(hook);
    }

    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // Only whole windows: LOCATIONCHANGE also fires for carets, cursors and child objects.
        if (hwnd == 0 || idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF)
        {
            return;
        }

        var service = s_instance;
        if (service is null || service._disposed)
        {
            return;
        }

        try
        {
            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    service.ForegroundChanged?.Invoke(hwnd);
                    break;
                case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                    service.LocationChanged?.Invoke(hwnd);
                    break;
                case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                    service.MinimizeStarted?.Invoke(hwnd);
                    break;
                case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                    service.MinimizeEnded?.Invoke(hwnd);
                    break;
                case NativeMethods.EVENT_OBJECT_DESTROY:
                    service.WindowDestroyed?.Invoke(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind into user32.
            Diagnostics.Log("WinEvent handler failed: " + ex);
        }
    }
}
