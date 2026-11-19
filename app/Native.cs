using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace RSPAudioRecorder;

/// <summary>Win32 pieces WPF does not expose: tool-window style, global hotkey, DWM.</summary>
internal static class Native
{
    public const int WM_HOTKEY = 0x0312;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

    // Hotkey stored like the Win32 hotkey control: LOBYTE = virtual key, HIBYTE = flags.
    public const byte HOTKEYF_SHIFT = 1, HOTKEYF_CONTROL = 2, HOTKEYF_ALT = 4;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    /// <summary>Keeps a window out of the taskbar, Alt+Tab and Task View.</summary>
    public static void MakeToolWindow(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
    }

    public static void RoundCorners(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(h, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    /// <summary>Title-bar colour as COLORREF (0x00BBGGRR).</summary>
    public static void TintCaption(Window w, int colorref)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        DwmSetWindowAttribute(h, 35 /* DWMWA_CAPTION_COLOR */, ref colorref, sizeof(int));
    }

    public static bool RegisterHotkey(IntPtr hwnd, int id, ushort hotkey)
    {
        UnregisterHotKey(hwnd, id);
        byte vk = (byte)(hotkey & 0xFF), f = (byte)(hotkey >> 8);
        if (vk == 0) return true;
        uint mods = MOD_NOREPEAT;
        if ((f & HOTKEYF_CONTROL) != 0) mods |= MOD_CONTROL;
        if ((f & HOTKEYF_SHIFT) != 0) mods |= MOD_SHIFT;
        if ((f & HOTKEYF_ALT) != 0) mods |= MOD_ALT;
        return RegisterHotKey(hwnd, id, mods, vk);
    }

    public static void UnregisterHotkey(IntPtr hwnd, int id) => UnregisterHotKey(hwnd, id);

    public static ushort HotkeyFrom(Key key, ModifierKeys mods)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        byte f = 0;
        if (mods.HasFlag(ModifierKeys.Control)) f |= HOTKEYF_CONTROL;
        if (mods.HasFlag(ModifierKeys.Shift)) f |= HOTKEYF_SHIFT;
        if (mods.HasFlag(ModifierKeys.Alt)) f |= HOTKEYF_ALT;
        return (ushort)((f << 8) | (vk & 0xFF));
    }

    public static string HotkeyText(ushort hotkey)
    {
        byte vk = (byte)(hotkey & 0xFF), f = (byte)(hotkey >> 8);
        if (vk == 0) return "None";
        var parts = new List<string>();
        if ((f & HOTKEYF_CONTROL) != 0) parts.Add("Ctrl");
        if ((f & HOTKEYF_SHIFT) != 0) parts.Add("Shift");
        if ((f & HOTKEYF_ALT) != 0) parts.Add("Alt");
        var key = KeyInterop.KeyFromVirtualKey(vk);
        string name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            _ => key.ToString(),
        };
        parts.Add(name);
        return string.Join(" + ", parts);
    }
}
