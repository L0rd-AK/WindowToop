using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Mullion.App.Services;
using Mullion.Core;

namespace Mullion.App.UI;

/// <summary>Hotkey bindings, toolbar toggle, opacity step and Start-with-Windows. Returns a new <see cref="AppSettings"/> on Save.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private readonly bool _startupWasEnabled;

    internal SettingsWindow(AppSettings current)
    {
        _working = current.Clone();
        InitializeComponent();

        TopmostBox.Text = _working.Hotkeys.ToggleTopmost;
        OpacityUpBox.Text = _working.Hotkeys.OpacityUp;
        OpacityDownBox.Text = _working.Hotkeys.OpacityDown;
        ClickThroughBox.Text = _working.Hotkeys.ToggleClickThrough;
        PipBox.Text = _working.Hotkeys.PictureInPicture;

        ToolbarCheck.IsChecked = _working.ToolbarEnabled;
        StepCombo.SelectedValue = _working.OpacityStep.ToString();
        if (StepCombo.SelectedIndex < 0)
        {
            StepCombo.SelectedIndex = 1;
        }

        _startupWasEnabled = StartupService.IsEnabled();
        StartupCheck.IsChecked = _startupWasEnabled;
    }

    /// <summary>The edited settings; valid only when the dialog closed with DialogResult == true.</summary>
    internal AppSettings Result => _working;

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox box)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return; // a lone modifier is not a hotkey yet
        }

        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false; // keep keyboard navigation working
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        var hotkey = new Hotkey(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        box.Text = hotkey.ToString();
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void OnClearHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox box })
        {
            box.Text = string.Empty;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var boxes = new (HotkeyAction Action, TextBox Box)[]
        {
            (HotkeyAction.ToggleTopmost, TopmostBox),
            (HotkeyAction.OpacityUp, OpacityUpBox),
            (HotkeyAction.OpacityDown, OpacityDownBox),
            (HotkeyAction.ToggleClickThrough, ClickThroughBox),
            (HotkeyAction.PictureInPicture, PipBox),
        };

        var seen = new Dictionary<Hotkey, HotkeyAction>();
        foreach (var (action, box) in boxes)
        {
            if (!Hotkey.TryParse(box.Text, out var hotkey))
            {
                ShowError($"'{box.Text}' is not a valid hotkey.");
                return;
            }

            if (!hotkey.IsEmpty && !hotkey.IsUsable)
            {
                ShowError($"{hotkey} needs a modifier (Ctrl, Alt, Shift or Win) unless it is a function key.");
                return;
            }

            if (!hotkey.IsEmpty && seen.TryGetValue(hotkey, out var other))
            {
                ShowError($"{hotkey} is assigned to both {other} and {action}.");
                return;
            }

            if (!hotkey.IsEmpty)
            {
                seen[hotkey] = action;
            }

            _working.Hotkeys.Set(action, hotkey.ToString());
        }

        _working.ToolbarEnabled = ToolbarCheck.IsChecked == true;
        _working.OpacityStep = int.TryParse(StepCombo.SelectedValue as string, out int step) ? step : 10;

        bool startup = StartupCheck.IsChecked == true;
        if (startup != _startupWasEnabled)
        {
            string? error = StartupService.TrySetEnabled(startup);
            if (error is not null)
            {
                ShowError("Start with Windows could not be changed: " + error);
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
