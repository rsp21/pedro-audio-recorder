using System.IO;
using Microsoft.Win32;

namespace RSPAudioRecorder;

/// <summary>Settings in HKCU\Software\RSP\AudioRecorder (shared with the earlier recorder).</summary>
internal static class AppSettings
{
    private const string Key = @"Software\RSP\AudioRecorder";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RSP Audio Recorder";
    public const ushort DefaultHotkey = (Native.HOTKEYF_CONTROL | Native.HOTKEYF_SHIFT) << 8 | 0x20; // Ctrl+Shift+Space

    public static ushort Hotkey { get => (ushort)Get("Hotkey", DefaultHotkey); set => Set("Hotkey", value); }
    public static bool LiveTranscript { get => Get("LiveTranscript", 1) != 0; set => Set("LiveTranscript", value ? 1 : 0); }
    public static bool RecordMic { get => Get("RecordMic", 1) != 0; set => Set("RecordMic", value ? 1 : 0); }
    public static bool OpenAfterSave { get => Get("OpenAfterSave", 1) != 0; set => Set("OpenAfterSave", value ? 1 : 0); }
    public static bool HideLiveCaptions { get => Get("HideLiveCaptions", 1) != 0; set => Set("HideLiveCaptions", value ? 1 : 0); }
    public static bool ShowTranscript { get => Get("ShowTranscript", 1) != 0; set => Set("ShowTranscript", value ? 1 : 0); }
    public static string FileName { get; set; } = "";   // per session, like before

    public const string EngineLiveCaptions = "livecaptions", EngineWhisper = "whisper";

    /// <summary>Transcription engine: Windows Live Captions (default) or Whisper.</summary>
    public static string Engine
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("Engine") as string == EngineWhisper ? EngineWhisper : EngineLiveCaptions;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("Engine", value, RegistryValueKind.String);
        }
    }

    public static string OutputFolder =>
        Environment.GetEnvironmentVariable("RSP_TRANSCRIPT_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "Music", "Audio Recorder");

    public static string? PythonPathOverride
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("PythonPath") as string;
        }
    }

    /// <summary>First run: start with Windows. Later runs: keep the entry pointing at this exe.</summary>
    public static void InitStartup()
    {
        if (Get("Initialized", 0) == 0)
        {
            StartWithWindows = true;
            Set("Initialized", 1);
        }
        else if (StartWithWindows) StartWithWindows = true;
    }

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue(RunValue, false);
        }
    }

    private static int Get(string name, int fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(name) is int v ? v : fallback;
    }

    private static void Set(string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }
}
