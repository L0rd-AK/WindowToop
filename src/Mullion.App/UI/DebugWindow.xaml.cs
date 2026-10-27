using System.Windows;
using System.Windows.Controls;
using Mullion.App.Interop;
using Mullion.Core;

namespace Mullion.App.UI;

/// <summary>
/// Developer console: lists eligible windows and drives the controller directly, logging every result.
/// Also the target of --selftest.
/// </summary>
public partial class DebugWindow : Window
{
    private readonly WindowController _controller;
    private readonly uint _ownProcessId;
    private Action<nint>? _pipRequest;
    private nint _target;
    private bool _syncing;

    internal DebugWindow(WindowController controller, Action<nint>? pipRequest = null)
    {
        _controller = controller;
        _ownProcessId = controller.OwnProcessId;
        _pipRequest = pipRequest;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            RefreshWindows();
            RefreshTracked();
        };
        _controller.Changed += OnControllerChanged;
        Closed += (_, _) => _controller.Changed -= OnControllerChanged;
    }

    internal Action<nint>? PipRequest
    {
        get => _pipRequest;
        set => _pipRequest = value;
    }

    public void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void OnControllerChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshTracked();
        }
        else
        {
            Dispatcher.BeginInvoke(RefreshTracked);
        }
    }

    private void RefreshWindows()
    {
        var items = new List<WindowListItem>();
        foreach (nint hwnd in Win32WindowOps.EnumerateTopLevelWindows())
        {
            var probe = _controller.Probe(hwnd);
            if (!probe.IsEligible || probe.Snapshot is null)
            {
                continue;
            }

            var snapshot = probe.Snapshot.Value;
            items.Add(new WindowListItem(hwnd, snapshot.Title, snapshot.ClassName, snapshot.ProcessId));
        }

        WindowList.ItemsSource = items;
        WindowList.SelectedItem = items.FirstOrDefault(i => i.Hwnd == _target);
    }

    private void RefreshTracked()
    {
        TrackedList.ItemsSource = _controller.TrackedWindows.Select(t => $"0x{t.Hwnd:X}  {t}").ToList();
        SyncTargetState();
    }

    private void SyncTargetState()
    {
        _syncing = true;
        try
        {
            TrackedWindow? tracked = _target == 0 ? null : _controller.Find(_target);
            OpacitySlider.Value = tracked?.Opacity ?? OpacityMath.Max;
            OpacityLabel.Text = $"{(int)OpacitySlider.Value}%";
            TopmostButton.Content = tracked?.IsTopmost == true ? "Always on top: ON (click to unpin)" : "Pin always on top";
            ClickThroughButton.Content = tracked?.ClickThrough == true ? "Click-through: ON (click to turn off)" : "Click-through: off";

            bool hasTarget = _target != 0;
            TopmostButton.IsEnabled = hasTarget;
            ClickThroughButton.IsEnabled = hasTarget;
            OpacitySlider.IsEnabled = hasTarget;
            RestoreButton.IsEnabled = hasTarget;
            PipButton.IsEnabled = hasTarget && _pipRequest is not null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshWindows();

    private void OnIncludeOwnChanged(object sender, RoutedEventArgs e)
    {
        _controller.OwnProcessId = IncludeOwnCheck.IsChecked == true ? 0 : _ownProcessId;
        RefreshWindows();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = WindowList.SelectedItem as WindowListItem;
        _target = item?.Hwnd ?? 0;
        TargetText.Text = item is null ? "Select a window on the left." : $"{item.Title}\n{item.ClassName}  0x{item.Hwnd:X}";
        SyncTargetState();
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e) => Report(_controller.ToggleTopmost(_target));

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _target == 0 || !IsLoaded)
        {
            return;
        }

        OpacityLabel.Text = $"{(int)e.NewValue}%";
        Report(_controller.SetOpacity(_target, (int)e.NewValue));
    }

    private void OnToggleClickThrough(object sender, RoutedEventArgs e) => Report(_controller.ToggleClickThrough(_target));

    private void OnPip(object sender, RoutedEventArgs e)
    {
        if (_pipRequest is null)
        {
            Log("Picture-in-picture is not available in this build.");
            return;
        }

        _pipRequest(_target);
    }

    private void OnRestore(object sender, RoutedEventArgs e) => Report(_controller.Restore(_target));

    private void OnRestoreAll(object sender, RoutedEventArgs e) => Log($"Restored {_controller.RestoreAll()} window(s).");

    private void Report(OpResult result)
    {
        Log($"[{result.Status}] {result.Message}{(result.Warning is null ? string.Empty : "  Warning: " + result.Warning)}");
        SyncTargetState();
    }
}

internal sealed record WindowListItem(nint Hwnd, string Title, string ClassName, uint ProcessId)
{
    public string Display => $"{Title}   [{ClassName}, pid {ProcessId}]";
}
