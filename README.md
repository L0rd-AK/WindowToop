# Mullion

Mullion gives any top-level window on Windows a few extra controls: keep it on top, fade it, let clicks
pass through it, or shrink it to a live picture-in-picture view. It is a small tray utility written in
C# / .NET 8 with WPF, talking to Windows directly through Win32 (no window-management libraries).

A mullion is the bar that holds window panes together, which is roughly what this does.

## Features

| Feature | How it works |
| --- | --- |
| Always on top | `SetWindowPos` with `HWND_TOPMOST` / `HWND_NOTOPMOST` (`SWP_NOMOVE \| SWP_NOSIZE \| SWP_NOACTIVATE`) |
| Opacity 10-100% | Adds `WS_EX_LAYERED`, then `SetLayeredWindowAttributes(..., LWA_ALPHA)`. At 100% the layered style is removed again. |
| Click-through | `WS_EX_TRANSPARENT` on top of `WS_EX_LAYERED`. Undo it with the hotkey, the toolbar, or the tray menu, since the window itself can no longer be clicked. |
| Global hotkeys | `RegisterHotKey` on a hidden message-only window (`HWND_MESSAGE`); `WM_HOTKEY` is handled in its WndProc. All hotkeys act on the foreground window. |
| Floating toolbar | A borderless, topmost, `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW` WPF window that appears when the mouse rests on the title bar of the foreground window. Windows are tracked with `SetWinEventHook`, the pointer with a `WH_MOUSE_LL` hook. |
| Tray icon | Lists every modified window with per-window controls, toggles the toolbar, opens settings, Start with Windows (HKCU `Run` key), Exit. |
| Picture-in-picture | A small topmost frame showing a live DWM thumbnail (`DwmRegisterThumbnail`). Clicking it brings the original window back to the front. |

Everything Mullion changes is tracked by window handle together with the original extended style and
topmost state. All of it is restored on Exit, when a window is destroyed, and from the unhandled-exception
and `ProcessExit` paths.

## Default hotkeys

| Hotkey | Action |
| --- | --- |
| `Ctrl+Alt+T` | Toggle always on top |
| `Ctrl+Alt+Up` | Opacity +10% |
| `Ctrl+Alt+Down` | Opacity -10% |
| `Ctrl+Alt+C` | Toggle click-through |
| `Ctrl+Alt+P` | Toggle picture-in-picture |

Change them in the tray menu under *Settings and hotkeys...*. If another program already owns a
combination, Windows refuses the registration and Mullion tells you which one in a notification.

## Build

Requirements: Windows 10 1607 or later, the .NET 8 SDK (a newer SDK such as 9.x builds it too; the .NET 8
Desktop Runtime must be installed to run framework-dependent builds).

```powershell
dotnet build Mullion.sln
dotnet test Mullion.sln
```

Run the app from the build output (`src\Mullion.App\bin\Debug\net8.0-windows\Mullion.exe`), or:

```powershell
dotnet run --project src\Mullion.App
```

### Publish (single file, framework-dependent)

```powershell
dotnet publish src\Mullion.App\Mullion.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The executable lands in `src\Mullion.App\bin\Release\net8.0-windows\win-x64\publish\Mullion.exe`.
Use `-r win-arm64` or `-r win-x86` for other architectures.

## Command-line switches

| Switch | Effect |
| --- | --- |
| `--debug` | Also open the debug console (list eligible windows, drive every operation by hand, see results). |
| `--settings <file>` | Use this settings file instead of `%APPDATA%\Mullion\settings.json` (portable use). |
| `--selftest [report]` | Exercise the real Win32 paths against Mullion's own windows, write a PASS/FAIL report, exit with 0 or 1. |
| `--smoke [ms]` | Start normally, then shut down cleanly after the delay. Useful for scripted checks. |

The debug console is also available from the tray menu.

## Settings

Stored as JSON in `%APPDATA%\Mullion\settings.json`:

```json
{
  "toolbarEnabled": true,
  "opacityStep": 10,
  "toolbarHideDelayMs": 600,
  "hotkeys": {
    "toggleTopmost": "Ctrl+Alt+T",
    "opacityUp": "Ctrl+Alt+Up",
    "opacityDown": "Ctrl+Alt+Down",
    "toggleClickThrough": "Ctrl+Alt+C",
    "pictureInPicture": "Ctrl+Alt+P"
  }
}
```

Hand edits are fine; out-of-range values are clamped and an unreadable file falls back to defaults.
A diagnostic log is written to `%LOCALAPPDATA%\Mullion\mullion.log`.

## Project layout

```
src/Mullion.Core     Platform-independent logic: window filter, state tracking and restore (WindowController),
                     hotkey parsing, settings, toolbar/thumbnail geometry. No WPF, no Win32.
src/Mullion.App      WPF tray application. Interop/NativeMethods.cs holds every P/Invoke; services wrap
                     hotkeys, WinEvent and mouse hooks, the toolbar, the tray, PiP and startup registration.
tests/Mullion.Tests  xunit tests for Mullion.Core, using an in-memory fake of the Win32 operations.
```

## Which windows are touched

Only real top-level, visible windows: `IsWindowVisible`, not `WS_EX_TOOLWINDOW`, not DWM-cloaked, with a
non-empty title, not part of the desktop shell, and never Mullion's own windows. Handles are walked up to
`GetAncestor(GA_ROOT)` first, so a hotkey pressed while a child control has focus still targets the
owning window.

## Known limitations

- **Elevated windows.** When Mullion runs as a normal user, `SetWindowPos` / `SetWindowLongPtr` on windows
  of elevated processes fail with `ERROR_ACCESS_DENIED` (UIPI). Mullion shows a notification; run it as
  administrator to control those windows.
- **WPF applications.** WPF windows strip `WS_EX_LAYERED` in their `WM_STYLECHANGING` handler unless the
  app itself uses per-pixel opacity, so opacity and click-through cannot be applied to them from outside.
  Mullion detects this by reading the style back, reports it as skipped, and leaves the window untouched.
  Always-on-top still works.
- **UWP / Store apps** (`ApplicationFrameWindow`). Opacity is applied to the host frame and often has no
  visible effect; Mullion warns when it targets one.
- **Windows that are already layered.** If an app draws its own layered window (custom splash screens,
  some launchers), Mullion leaves its opacity alone rather than fighting the app's own
  `UpdateLayeredWindow` calls. Click-through still works there.
- **Picture-in-picture of a minimized window.** DWM stops updating thumbnails of minimized windows, so the
  view freezes until the window is restored. Mullion does not move or hide the source window.
- **Click-through and focus.** A click-through window cannot be focused with the mouse, so a hotkey will
  only reach it while it still has keyboard focus. The floating toolbar and the tray menu always work.
- **Hotkey conflicts.** Combinations already registered by other software cannot be taken over; pick
  another one in settings.
- **Low-level hook timeout.** Windows silently removes a `WH_MOUSE_LL` hook whose callback is too slow.
  Mullion's callback only records the pointer position and posts to the dispatcher, but a heavily
  overloaded machine could still lose the hook; restarting Mullion reinstalls it.
- **Single instance.** A second launch shows a message and exits.

## Verification

- `dotnet test` runs 144 unit tests against the core logic (filtering, state tracking, restore ordering,
  style-rejection rollback, hotkey parsing, settings persistence, hover and thumbnail geometry).
- `Mullion.exe --selftest report.txt --settings temp.json` drives the real Win32 paths: a WinForms and a
  WPF target window, raw style probes, the controller, hotkey registration and `WM_HOTKEY` dispatch, tray
  creation, WinEvent and mouse hooks, toolbar placement and hover logic, DWM thumbnails, settings round
  trip, and restore-on-exit through the host's teardown.
