namespace Mullion.Core;

public enum OpStatus
{
    /// <summary>The change was applied.</summary>
    Ok,

    /// <summary>The window failed <see cref="WindowFilter"/>; nothing was changed.</summary>
    NotEligible,

    /// <summary>UIPI blocked the call: the target is elevated and we are not.</summary>
    AccessDenied,

    /// <summary>The change was deliberately not applied (e.g. the app already uses a layered window).</summary>
    Skipped,

    /// <summary>A Win32 call failed for another reason.</summary>
    Failed,
}

/// <summary>Outcome of a controller operation, with a user-facing message and optional warning.</summary>
public sealed record OpResult(OpStatus Status, string Message, TrackedWindow? Window = null, string? Warning = null)
{
    public bool Succeeded => Status == OpStatus.Ok;

    public static OpResult Ok(TrackedWindow window, string message, string? warning = null) => new(OpStatus.Ok, message, window, warning);

    public static OpResult NotEligible(FilterVerdict verdict) => new(OpStatus.NotEligible, $"Can't modify this window: {WindowFilter.Describe(verdict)}.");

    public static OpResult AccessDenied(TrackedWindow? window) => new(
        OpStatus.AccessDenied,
        "Windows refused the change (access denied). The target runs elevated; run Mullion as administrator to control it.",
        window);

    public static OpResult Skipped(TrackedWindow window, string message) => new(OpStatus.Skipped, message, window);

    public static OpResult Failed(TrackedWindow? window, int error, string what) => new(OpStatus.Failed, $"{what} failed (Win32 error {error}).", window);
}
