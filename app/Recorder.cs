using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace RSPAudioRecorder;

/// <summary>
/// Drives pedro-audio-recorder-core.exe (capture + mix) and live_transcriber.py
/// (faster-whisper) through named events and a shared level buffer.
/// </summary>
internal sealed class Recorder
{
    public enum RecState { Idle, Recording, Paused }

    public RecState State { get; private set; } = RecState.Idle;
    public string? Output { get; private set; }
    public string? LastSaved { get; private set; }
    public bool TranscriberRunning { get; private set; }

    private Process? _core;
    private EventWaitHandle? _stop, _pause;
    private MemoryMappedFile? _levelsMap;
    private MemoryMappedViewAccessor? _levels;
    private readonly Stopwatch _clock = new();

    private static string Dir => AppContext.BaseDirectory;
    private static readonly string Id = Environment.ProcessId.ToString();

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Peak level (0..1) of PC audio and microphone, written by the core.</summary>
    public float Level
    {
        get
        {
            if (_levels == null) return 0;
            try { return Math.Max(_levels.ReadSingle(0), _levels.ReadSingle(4)); }
            catch (ObjectDisposedException) { return 0; }
        }
    }

    /// <summary>Starts a recording. Returns an error message, or null.</summary>
    public string? Start(string fileName, bool recordMic, bool transcribe)
    {
        if (_core != null) return null;
        var folder = AppSettings.OutputFolder;
        Directory.CreateDirectory(folder);
        string name = string.IsNullOrWhiteSpace(fileName) || fileName == "(timestamp)"
            ? $"audio-{DateTime.Now:yyyyMMdd-HHmmss}.wav" : fileName.Trim();
        string output = name.IndexOfAny(new[] { '\\', '/' }) >= 0 ? name : Path.Combine(folder, name);
        if (!Path.HasExtension(output)) output += ".wav";

        string stopName = $@"Local\RSPAudioStop_{Id}", pauseName = $@"Local\RSPAudioPause_{Id}";
        try
        {
            _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
            _pause = new EventWaitHandle(false, EventResetMode.ManualReset, pauseName);
            _stop.Reset();
            _pause.Reset();
            _levelsMap = MemoryMappedFile.CreateOrOpen($@"Local\RSPAudioLevels_{Id}", 8);
            _levels = _levelsMap.CreateViewAccessor(0, 8);
            _levels.Write(0, 0f);
            _levels.Write(4, 0f);
        }
        catch (Exception ex)
        {
            Cleanup();
            return "Control setup failed: " + ex.Message;
        }

        var core = Path.Combine(Dir, "pedro-audio-recorder-core.exe");
        var args = $"\"{output}\" --stop-event \"{stopName}\" --pause-event \"{pauseName}\" --control-id \"{Id}\"" +
                   (recordMic ? "" : " --no-mic");
        try
        {
            _core = Process.Start(new ProcessStartInfo(core, args) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Cleanup();
            return "Recorder start failed: " + ex.Message;
        }

        Output = output;
        State = RecState.Recording;
        _clock.Restart();
        TranscriberRunning = transcribe && StartTranscriber(output, stopName);
        return null;
    }

    public void TogglePause()
    {
        if (_core == null) return;
        if (State == RecState.Recording)
        {
            _pause!.Set();
            _clock.Stop();
            State = RecState.Paused;
        }
        else if (State == RecState.Paused)
        {
            _pause!.Reset();
            _clock.Start();
            State = RecState.Recording;
        }
    }

    /// <summary>Stops and finalises the file. The transcriber finishes on its own.</summary>
    public async Task<string?> StopAsync()
    {
        if (_core == null) return null;
        _stop!.Set();
        var core = _core;
        await Task.Run(() =>
        {
            if (!core.WaitForExit(5000)) { try { core.Kill(); } catch { } }
        });
        var saved = Output;
        LastSaved = saved;
        Cleanup();
        return saved;
    }

    /// <summary>Synchronous stop for app exit / sign-out.</summary>
    public void StopNow()
    {
        if (_core == null) return;
        _stop!.Set();
        if (!_core.WaitForExit(5000)) { try { _core.Kill(); } catch { } }
        Cleanup();
    }

    /// <summary>True when the core exited by itself (device error etc.).</summary>
    public bool ExitedUnexpectedly(out int code)
    {
        code = 0;
        if (_core == null || !_core.HasExited) return false;
        code = _core.ExitCode;
        LastSaved = code == 0 ? Output : LastSaved;
        Cleanup();
        return true;
    }

    private void Cleanup()
    {
        _core?.Dispose();
        _core = null;
        _stop?.Dispose(); _stop = null;
        _pause?.Dispose(); _pause = null;
        _levels?.Dispose(); _levels = null;
        _levelsMap?.Dispose(); _levelsMap = null;
        _clock.Reset();
        State = RecState.Idle;
        TranscriberRunning = false;
    }

    // ------------------------------------------------------------------ transcriber

    /// <summary>RSP_PYTHON, else the PythonPath setting, else the RSP-Transcript virtual environment.</summary>
    public static string? PythonPath()
    {
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("RSP_PYTHON"),
                     AppSettings.PythonPathOverride,
                     Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "RSP-Transcript", ".venv", "Scripts", "python.exe"),
                 })
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
        return null;
    }

    private static string? ScriptPath()
    {
        foreach (var p in new[] { Path.Combine(Dir, "live_transcriber.py"), Path.Combine(Dir, "..", "transcriber", "live_transcriber.py") })
            if (File.Exists(p)) return Path.GetFullPath(p);
        return null;
    }

    private static bool StartTranscriber(string wav, string stopEvent)
    {
        string? py = PythonPath(), script = ScriptPath();
        if (py == null || script == null) return false;
        try
        {
            var p = Process.Start(new ProcessStartInfo(py,
                $"-I \"{script}\" --wav \"{wav}\" --stop-event \"{stopEvent}\" --parent-pid {Environment.ProcessId}")
            { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return false;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            p.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
