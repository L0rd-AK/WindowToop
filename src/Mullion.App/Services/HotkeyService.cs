using System.Runtime.InteropServices;
using Mullion.App.Interop;
using Mullion.Core;

namespace Mullion.App.Services;

internal sealed record HotkeyRegistrationFailure(HotkeyAction Action, Hotkey Hotkey, int Error)
{
    public string Message => Error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
        ? $"{Hotkey} ({Action}) is already used by another program."
        : $"{Hotkey} ({Action}) could not be registered (Win32 error {Error}).";
}

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message-only window (HWND_MESSAGE). WM_HOTKEY arrives in
/// our WndProc on the UI thread because the window is created on it and WPF pumps its messages.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const string ClassName = "MullionHotkeyWindow";

    // The WndProc thunk must outlive the window; a static field guarantees the delegate is never collected.
    private static readonly NativeMethods.WndProc WndProcThunk = StaticWndProc;
    private static HotkeyService? s_instance;

    private readonly nint _hwnd;
    private readonly nint _instance;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private bool _classRegistered;
    private bool _disposed;

    public HotkeyService()
    {
        if (s_instance is not null)
        {
            throw new InvalidOperationException("Only one HotkeyService may exist per process.");
        }

        _instance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            WndProc = WndProcThunk,
            Instance = _instance,
            ClassName = ClassName,
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            const int ErrorClassAlreadyExists = 1410;
            if (error != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException($"RegisterClassEx failed (Win32 error {error}).");
            }
        }
        else
        {
            _classRegistered = true;
        }

        _hwnd = NativeMethods.CreateWindowEx(0, ClassName, "Mullion hotkeys", 0, 0, 0, 0, 0, NativeMethods.HWND_MESSAGE, 0, _instance, 0);
        if (_hwnd == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"CreateWindowEx(HWND_MESSAGE) failed (Win32 error {error}).");
        }

        s_instance = this;
    }

    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    public event Action<HotkeyAction>? Pressed;

    public nint Handle => _hwnd;

    public int RegisteredCount => _registered.Count;

    /// <summary>Actions whose binding Windows accepted, with the id WM_HOTKEY will carry for each.</summary>
    public IReadOnlyDictionary<int, HotkeyAction> Registered => _registered;

    /// <summary>Replaces all registrations with the given bindings. Returns the ones Windows refused.</summary>
    public IReadOnlyList<HotkeyRegistrationFailure> Register(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UnregisterAll();

        var failures = new List<HotkeyRegistrationFailure>();
        foreach (var (action, hotkey) in settings.Enumerate())
        {
            if (!hotkey.IsUsable)
            {
                continue;
            }

            int id = (int)action + 1;
            uint modifiers = (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT;
            if (NativeMethods.RegisterHotKey(_hwnd, id, modifiers, hotkey.VirtualKey))
            {
                _registered[id] = action;
            }
            else
            {
                failures.Add(new HotkeyRegistrationFailure(action, hotkey, Marshal.GetLastWin32Error()));
            }
        }

        return failures;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered.Keys)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        if (_hwnd != 0)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }

        if (_classRegistered)
        {
            NativeMethods.UnregisterClass(ClassName, _instance);
        }

        s_instance = null;
    }

    private static nint StaticWndProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WM_HOTKEY && s_instance is { } service)
        {
            service.OnHotkey((int)wParam);
            return 0;
        }

        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void OnHotkey(int id)
    {
        if (!_registered.TryGetValue(id, out var action))
        {
            return;
        }

        try
        {
            Pressed?.Invoke(action);
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the native call stack.
            Diagnostics.Log("Hotkey handler failed: " + ex);
        }
    }
}
