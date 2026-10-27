namespace Mullion.Core;

/// <summary>Command-line switches. Everything is optional; a bare launch runs the tray app.</summary>
public sealed class LaunchOptions
{
    /// <summary>--debug: open the debug console at startup.</summary>
    public bool ShowDebugWindow { get; private set; }

    /// <summary>--selftest [report-file]: exercise the real Win32 paths against Mullion's own windows, write a report, exit.</summary>
    public bool SelfTest => SelfTestReportPath is not null;

    public string? SelfTestReportPath { get; private set; }

    /// <summary>--smoke [ms]: start normally (tray, hotkeys, hooks, toolbar), then shut down cleanly after the delay.</summary>
    public int SmokeDurationMs { get; private set; }

    /// <summary>--settings &lt;file&gt;: use this settings file instead of %APPDATA%\Mullion\settings.json (portable use, tests).</summary>
    public string? SettingsPath { get; private set; }

    /// <summary>--pipcheck [report-file]: end-to-end check that PiP stays live while a real browser window is covered.</summary>
    public bool PipCheck => PipCheckReportPath is not null;

    public string? PipCheckReportPath { get; private set; }

    /// <summary>--browser &lt;exe&gt;: Chromium-based browser for --pipcheck (default: Edge, then Chrome).</summary>
    public string? BrowserPath { get; private set; }

    public static LaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new LaunchOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith('/'))
            {
                arg = "--" + arg[1..];
            }

            switch (arg.ToLowerInvariant())
            {
                case "--debug":
                    options.ShowDebugWindow = true;
                    break;

                case "--selftest":
                    options.SelfTestReportPath = TakeValue(args, ref i) ?? Path.Combine(Path.GetTempPath(), "mullion-selftest.txt");
                    break;

                case "--smoke":
                    string? ms = TakeValue(args, ref i);
                    options.SmokeDurationMs = ms is not null && int.TryParse(ms, out int parsed) ? Math.Max(100, parsed) : 3000;
                    break;

                case "--settings":
                    options.SettingsPath = TakeValue(args, ref i);
                    break;

                case "--pipcheck":
                    options.PipCheckReportPath = TakeValue(args, ref i) ?? Path.Combine(Path.GetTempPath(), "mullion-pipcheck.txt");
                    break;

                case "--browser":
                    options.BrowserPath = TakeValue(args, ref i);
                    break;
            }
        }

        return options;
    }

    /// <summary>Consumes the next argument as a value unless it is itself a switch.</summary>
    private static string? TakeValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        string next = args[index + 1];
        if (next.StartsWith("--", StringComparison.Ordinal) || next.StartsWith('/'))
        {
            return null;
        }

        index++;
        return next;
    }
}
