using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RSPAudioRecorder;

public partial class SettingsWindow : Window
{
    private readonly IntPtr _mainHwnd;
    private readonly int _hotkeyId;
    private ushort _hotkey;

    public bool QuitRequested { get; private set; }

    public SettingsWindow(IntPtr mainHwnd, int hotkeyId)
    {
        InitializeComponent();
        _mainHwnd = mainHwnd;
        _hotkeyId = hotkeyId;
        // Owned by the main window, so it stays out of the taskbar and Alt+Tab.
        SourceInitialized += (_, _) => Native.TintCaption(this, 0x00FFFFFF);

        FileNameBox.Text = AppSettings.FileName;
        FolderHint.Text = "Recordings and transcripts are saved in " + AppSettings.OutputFolder + ".";
        OpenAfterBox.IsChecked = AppSettings.OpenAfterSave;
        TranscriptBox.IsChecked = AppSettings.LiveTranscript;
        MicBox.IsChecked = AppSettings.RecordMic;
        StartupBox.IsChecked = AppSettings.StartWithWindows;
        _hotkey = AppSettings.Hotkey;
        HotkeyBox.Text = Native.HotkeyText(_hotkey);
        Loaded += (_, _) => { FileNameBox.Focus(); FileNameBox.SelectAll(); };
    }

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        FileNameHint.Visibility = FileNameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        HotkeyHint.Text = "Press the new shortcut now (it needs Ctrl or Alt).";

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        HotkeyHint.Text = "Click the box and press a new key combination.";

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab) return;                       // let Tab move focus
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return;
        var mods = Keyboard.Modifiers;
        if (!mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Alt))
        {
            HotkeyHint.Text = "Use Ctrl or Alt with the key, for example Ctrl + Shift + Space.";
            return;
        }
        _hotkey = Native.HotkeyFrom(key, mods);
        HotkeyBox.Text = Native.HotkeyText(_hotkey);
        HotkeyHint.Text = "Click Save to use this shortcut.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Check the shortcut is free (the main window re-registers it after the dialog closes).
        if (!Native.RegisterHotkey(_mainHwnd, _hotkeyId, _hotkey))
        {
            MessageBox.Show(this, "That shortcut is already used by another app. Choose a different one.",
                            "Keyboard shortcut", MessageBoxButton.OK, MessageBoxImage.Warning);
            HotkeyBox.Focus();
            return;
        }
        Native.UnregisterHotkey(_mainHwnd, _hotkeyId);

        AppSettings.Hotkey = _hotkey;
        AppSettings.FileName = FileNameBox.Text.Trim();
        AppSettings.OpenAfterSave = OpenAfterBox.IsChecked == true;
        AppSettings.LiveTranscript = TranscriptBox.IsChecked == true;
        AppSettings.RecordMic = MicBox.IsChecked == true;
        AppSettings.StartWithWindows = StartupBox.IsChecked == true;
        DialogResult = true;
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.OutputFolder);
        Process.Start(new ProcessStartInfo(AppSettings.OutputFolder) { UseShellExecute = true });
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow &&
            MessageBox.Show(this, "Quit RSP Audio Recorder? A recording in progress is stopped and saved, and " +
                                  Native.HotkeyText(AppSettings.Hotkey) + " stops working until you start it again.",
                            "Quit", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        QuitRequested = true;
        DialogResult = false;
    }
}
