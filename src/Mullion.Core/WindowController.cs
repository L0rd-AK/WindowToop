namespace Mullion.Core;

/// <summary>
/// Applies topmost / opacity / click-through changes to foreign windows and remembers what it changed
/// so everything can be undone. All Win32 access goes through <see cref="IWindowOps"/>, which keeps
/// this class testable without a desktop.
/// </summary>
public sealed class WindowController
{
    private readonly IWindowOps _ops;
    private readonly Dictionary<nint, TrackedWindow> _tracked = new();
    private readonly object _gate = new();

    public WindowController(IWindowOps ops, uint ownProcessId)
    {
        _ops = ops ?? throw new ArgumentNullException(nameof(ops));
        OwnProcessId = ownProcessId;
    }

    /// <summary>Process id whose windows are never modified. 0 disables the check (used by --selftest).</summary>
    public uint OwnProcessId { get; set; }

    /// <summary>Raised after any operation that may have changed tracked state. Fired on the calling thread.</summary>
    public event Action? Changed;

    /// <summary>Snapshot of every window Mullion currently has modified.</summary>
    public IReadOnlyList<TrackedWindow> TrackedWindows
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Values.ToArray();
            }
        }
    }

    public int TrackedCount
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Count;
            }
        }
    }

    /// <summary>Returns the tracked record for a handle (or its root), or null if it is unmodified.</summary>
    public TrackedWindow? Find(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_tracked.TryGetValue(hwnd, out var direct))
            {
                return direct;
            }

            nint root = _ops.GetRootWindow(hwnd);
            return root != 0 && _tracked.TryGetValue(root, out var viaRoot) ? viaRoot : null;
        }
    }

    /// <summary>Resolves a handle to its root window and runs the eligibility filter. Does not modify anything.</summary>
    public ProbeResult Probe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return new ProbeResult(0, FilterVerdict.NullHandle, null);
        }

        nint root = _ops.GetRootWindow(hwnd);
        if (root == 0)
        {
            root = hwnd;
        }

        WindowSnapshot? snapshot = _ops.TryGetSnapshot(root);
        if (snapshot is null)
        {
            return new ProbeResult(root, FilterVerdict.NullHandle, null);
        }

        lock (_gate)
        {
            // A window we already modified stays addressable even if (say) its title became empty.
            if (_tracked.ContainsKey(root))
            {
                return new ProbeResult(root, FilterVerdict.Eligible, snapshot);
            }
        }

        return new ProbeResult(root, WindowFilter.Evaluate(snapshot.Value, OwnProcessId), snapshot);
    }

    public int GetOpacity(nint hwnd) => Find(hwnd)?.Opacity ?? OpacityMath.Max;

    public OpResult ToggleTopmost(nint hwnd) => Apply(hwnd, (tw, snap) => SetTopmostCore(tw, !snap.IsTopmost));

    public OpResult SetTopmost(nint hwnd, bool topmost) => Apply(hwnd, (tw, _) => SetTopmostCore(tw, topmost));

    public OpResult SetOpacity(nint hwnd, int percent) => Apply(hwnd, (tw, snap) => SetOpacityCore(tw, snap, percent));

    /// <summary>Steps opacity by <paramref name="delta"/> percent (negative to fade out).</summary>
    public OpResult AdjustOpacity(nint hwnd, int delta) => Apply(hwnd, (tw, snap) => SetOpacityCore(tw, snap, OpacityMath.Step(tw.Opacity, delta)));

    public OpResult ToggleClickThrough(nint hwnd) => Apply(hwnd, (tw, snap) => SetClickThroughCore(tw, snap, (snap.ExStyle & WindowStyles.WS_EX_TRANSPARENT) == 0));

    public OpResult SetClickThrough(nint hwnd, bool enabled) => Apply(hwnd, (tw, snap) => SetClickThroughCore(tw, snap, enabled));

    /// <summary>Puts one window back to its original state and stops tracking it.</summary>
    public OpResult Restore(nint hwnd)
    {
        OpResult result;
        lock (_gate)
        {
            nint root = hwnd == 0 ? 0 : _ops.GetRootWindow(hwnd);
            if (root == 0)
            {
                root = hwnd;
            }

            if (!_tracked.TryGetValue(root, out var tw))
            {
                return new OpResult(OpStatus.Skipped, "That window has not been modified.");
            }

            result = RestoreCore(tw);
            if (result.Succeeded || !_ops.IsWindow(root))
            {
                _tracked.Remove(root);
            }
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>Restores every tracked window. Safe to call repeatedly and from shutdown paths.</summary>
    public int RestoreAll()
    {
        int restored = 0;
        lock (_gate)
        {
            foreach (var tw in _tracked.Values.ToArray())
            {
                try
                {
                    if (RestoreCore(tw).Succeeded)
                    {
                        restored++;
                    }
                }
                catch
                {
                    // Best effort: a window may vanish mid-way. Keep restoring the rest.
                }
            }

            _tracked.Clear();
        }

        Changed?.Invoke();
        return restored;
    }

    /// <summary>Stops tracking a window that has been destroyed (nothing can be restored any more).</summary>
    public void Forget(nint hwnd)
    {
        bool removed;
        lock (_gate)
        {
            removed = _tracked.Remove(hwnd);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Drops tracked entries whose window no longer exists. Returns how many were dropped.</summary>
    public int Prune()
    {
        int dropped = 0;
        lock (_gate)
        {
            foreach (var hwnd in _tracked.Keys.ToArray())
            {
                if (!_ops.IsWindow(hwnd))
                {
                    _tracked.Remove(hwnd);
                    dropped++;
                }
            }
        }

        if (dropped > 0)
        {
            Changed?.Invoke();
        }

        return dropped;
    }

    private OpResult Apply(nint hwnd, Func<TrackedWindow, WindowSnapshot, OpResult> operation)
    {
        OpResult result;
        lock (_gate)
        {
            var probe = Probe(hwnd);
            if (probe.Verdict != FilterVerdict.Eligible || probe.Snapshot is null)
            {
                return OpResult.NotEligible(probe.Verdict);
            }

            WindowSnapshot snapshot = probe.Snapshot.Value;
            if (!_tracked.TryGetValue(probe.Root, out var tw))
            {
                tw = new TrackedWindow(probe.Root, snapshot.Title, snapshot.ClassName, snapshot.ExStyle);
                _tracked[probe.Root] = tw;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.Title))
            {
                tw.Title = snapshot.Title;
            }

            result = operation(tw, snapshot);

            // Nothing left to undo: forget the window so the "modified" list stays honest.
            if (!tw.IsModified)
            {
                _tracked.Remove(probe.Root);
            }
        }

        Changed?.Invoke();
        return result;
    }

    private OpResult SetTopmostCore(TrackedWindow tw, bool topmost)
    {
        if (!_ops.SetTopmost(tw.Hwnd, topmost))
        {
            return Failure(tw, _ops.LastError, "SetWindowPos");
        }

        tw.IsTopmost = topmost;
        return OpResult.Ok(tw, topmost ? "Pinned on top." : "Unpinned.");
    }

    private OpResult SetOpacityCore(TrackedWindow tw, in WindowSnapshot snapshot, int percent)
    {
        percent = OpacityMath.Clamp(percent);
        if (tw.OriginallyLayered)
        {
            return OpResult.Skipped(tw, "This app already draws a layered window, so Mullion leaves its opacity alone.");
        }

        uint exStyle = snapshot.ExStyle;
        nint hwnd = tw.Hwnd;

        if (percent < OpacityMath.Max)
        {
            bool addedLayered = false;
            if ((exStyle & WindowStyles.WS_EX_LAYERED) == 0)
            {
                if (!TrySetExStyle(hwnd, exStyle, exStyle | WindowStyles.WS_EX_LAYERED, out int error))
                {
                    return Failure(tw, error, "SetWindowLongPtr");
                }

                addedLayered = true;
            }

            if (!_ops.SetLayeredAlpha(hwnd, OpacityMath.ToAlpha(percent)))
            {
                var failure = Failure(tw, _ops.LastError, "SetLayeredWindowAttributes");
                if (addedLayered)
                {
                    // A layered window with no attributes stops painting; undo the style change.
                    _ops.SetExStyle(hwnd, exStyle);
                }

                return failure;
            }

            tw.Opacity = percent;
            string? warning = tw.IsUwpFrame ? "Store (UWP) app windows often ignore opacity; this one may stay solid." : null;
            return OpResult.Ok(tw, $"Opacity {percent}%.", warning);
        }

        // Back to 100%: drop the layered style unless click-through still needs it.
        bool clickThroughActive = tw.ClickThrough || (exStyle & WindowStyles.WS_EX_TRANSPARENT) != 0;
        if ((exStyle & WindowStyles.WS_EX_LAYERED) != 0)
        {
            if (clickThroughActive)
            {
                if (!_ops.SetLayeredAlpha(hwnd, byte.MaxValue))
                {
                    return Failure(tw, _ops.LastError, "SetLayeredWindowAttributes");
                }
            }
            else
            {
                if (!_ops.SetExStyle(hwnd, exStyle & ~WindowStyles.WS_EX_LAYERED))
                {
                    return Failure(tw, _ops.LastError, "SetWindowLongPtr");
                }

                _ops.Redraw(hwnd);
            }
        }

        tw.Opacity = OpacityMath.Max;
        return OpResult.Ok(tw, "Opacity 100%.");
    }

    private OpResult SetClickThroughCore(TrackedWindow tw, in WindowSnapshot snapshot, bool enabled)
    {
        uint exStyle = snapshot.ExStyle;
        nint hwnd = tw.Hwnd;

        if (enabled)
        {
            uint target = exStyle | WindowStyles.WS_EX_TRANSPARENT;
            bool needLayered = (exStyle & WindowStyles.WS_EX_LAYERED) == 0;
            if (needLayered)
            {
                target |= WindowStyles.WS_EX_LAYERED;
            }

            if (!TrySetExStyle(hwnd, exStyle, target, out int error))
            {
                return Failure(tw, error, "SetWindowLongPtr");
            }

            // Freshly layered windows only paint once attributes are set; keep whatever opacity we track.
            if (needLayered && !_ops.SetLayeredAlpha(hwnd, OpacityMath.ToAlpha(tw.Opacity)))
            {
                var failure = Failure(tw, _ops.LastError, "SetLayeredWindowAttributes");
                _ops.SetExStyle(hwnd, exStyle);
                return failure;
            }

            tw.ClickThrough = true;
            return OpResult.Ok(tw, "Click-through on. Use the hotkey or the tray menu to turn it off.");
        }
        else
        {
            uint target = exStyle & ~WindowStyles.WS_EX_TRANSPARENT;
            bool dropLayered = !tw.OriginallyLayered
                && tw.Opacity == OpacityMath.Max
                && (exStyle & WindowStyles.WS_EX_LAYERED) != 0;
            if (dropLayered)
            {
                target &= ~WindowStyles.WS_EX_LAYERED;
            }

            if (target != exStyle && !_ops.SetExStyle(hwnd, target))
            {
                return Failure(tw, _ops.LastError, "SetWindowLongPtr");
            }

            if (dropLayered)
            {
                _ops.Redraw(hwnd);
            }

            tw.ClickThrough = false;
            return OpResult.Ok(tw, "Click-through off.");
        }
    }

    private OpResult RestoreCore(TrackedWindow tw)
    {
        nint hwnd = tw.Hwnd;
        if (!_ops.IsWindow(hwnd))
        {
            return OpResult.Failed(tw, 0, "Restore (window no longer exists)");
        }

        uint exStyle = _ops.GetExStyle(hwnd);
        bool ok = true;
        int firstError = 0;

        // Style bits first, z-order second: SetWindowLongPtr writes the whole value, so doing it after
        // SetWindowPos would reapply a stale WS_EX_TOPMOST bit. Only the bits we manage are put back;
        // anything the app changed in the meantime is left alone.
        uint target = (exStyle & ~WindowStyles.ManagedBits) | (tw.OriginalExStyle & WindowStyles.ManagedBits);
        if (target != exStyle)
        {
            if (_ops.SetExStyle(hwnd, target))
            {
                _ops.Redraw(hwnd);
            }
            else
            {
                ok = false;
                firstError = _ops.LastError;
            }
        }

        bool liveTopmost = (exStyle & WindowStyles.WS_EX_TOPMOST) != 0;
        if (liveTopmost != tw.OriginallyTopmost && !_ops.SetTopmost(hwnd, tw.OriginallyTopmost))
        {
            ok = false;
            firstError = firstError == 0 ? _ops.LastError : firstError;
        }

        tw.IsTopmost = tw.OriginallyTopmost;
        tw.Opacity = OpacityMath.Max;
        tw.ClickThrough = false;

        if (!ok)
        {
            return firstError == IWindowOps.ErrorAccessDenied ? OpResult.AccessDenied(tw) : OpResult.Failed(tw, firstError, "Restore");
        }

        return OpResult.Ok(tw, "Restored.");
    }

    /// <summary>
    /// Writes an ex-style. If the window silently rejected the managed bits, puts the original value back so
    /// no half-applied combination (e.g. WS_EX_TRANSPARENT without WS_EX_LAYERED) is left behind.
    /// </summary>
    private bool TrySetExStyle(nint hwnd, uint original, uint target, out int error)
    {
        if (_ops.SetExStyle(hwnd, target))
        {
            error = 0;
            return true;
        }

        error = _ops.LastError;
        if (error == IWindowOps.ErrorStyleRejected)
        {
            _ops.SetExStyle(hwnd, original);
        }

        return false;
    }

    private static OpResult Failure(TrackedWindow tw, int error, string what) => error switch
    {
        IWindowOps.ErrorAccessDenied => OpResult.AccessDenied(tw),
        IWindowOps.ErrorStyleRejected => OpResult.Skipped(
            tw,
            "This app rejected the layered window style (WPF apps manage it themselves), so opacity and click-through can't be applied to it. Always-on-top still works."),
        _ => OpResult.Failed(tw, error, what),
    };
}

/// <summary>Result of <see cref="WindowController.Probe"/>.</summary>
public readonly record struct ProbeResult(nint Root, FilterVerdict Verdict, WindowSnapshot? Snapshot)
{
    public bool IsEligible => Verdict == FilterVerdict.Eligible;
}
