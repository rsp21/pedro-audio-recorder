using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;

namespace RSPAudioRecorder;

/// <summary>
/// Live transcript from Windows 11 Live Captions: the same on-device speech engine,
/// read through UI Automation (there is no public API for it). Live Captions shows
/// its text in a TextBlock (AutomationId "CaptionsTextBlock"), one paragraph per line;
/// the last line is still being revised, earlier lines are final.
///
/// Live Captions mixes PC audio and (with "Include microphone audio" on) the mic, so
/// each sentence is labelled by which source was louder while its words appeared,
/// using the per-source levels the recorder core publishes.
///
/// Output uses the same files as the Whisper worker: &lt;name&gt;.transcript.jsonl and
/// &lt;name&gt;.live.json (with "parts": the live paragraph as ordered (source, text) pieces).
/// </summary>
internal sealed class LiveCaptionsEngine : IDisposable
{
    private const int PollMs = 50;
    private const double AudioLeadSeconds = 1.2;   // words show up ~0.5–1 s after they are spoken
    private const double AudioTailSeconds = 0.3;
    private const float SpeechLevel = 0.006f;      // RMS above this counts as someone talking

    /// <summary>Raised (on a worker thread) whenever the transcript or live line changed.</summary>
    public event Action? Changed;

    public string Stem { get; }
    private readonly Recorder _recorder;
    private readonly bool _labelSources;
    private readonly string _jsonl, _live;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _paused;
    private double _lastElapsed;   // recording time, kept in case the recorder stops first
    private int _committedCount;
    private readonly object _gate = new();

    // Level history: (recording time, PC level, mic level), sampled every PollMs.
    private readonly List<(double t, float pc, float mic)> _levels = new();

    // Caption tracking.
    private AutomationElement? _textBlock;
    private bool _baselineTaken;
    private string? _lastCommitted;          // text of the last committed line (anchor)
    private int _lastCommittedIndex = -1;    // ...and its line position in the caption text
    private string? _skipPrefix;             // text already on screen when recording started
    private string _activeLine = "";
    private readonly List<double> _activeWordTimes = new();
    private string? _lastSource;
    private string _status = "loading", _message = "Starting Live Captions…";
    private string _lastLiveJson = "";
    private DateTime _lastLiveWrite = DateTime.MinValue;

    // Live Captions' own window: hidden while recording (it keeps captioning), then restored,
    // or closed again if this app started it.
    private readonly bool _hideWindow, _startedByUs;
    private IntPtr _lcWindow;
    private bool _hiddenByUs;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public LiveCaptionsEngine(Recorder recorder, string wavPath, bool labelSources, bool hideWindow, bool startedByUs)
    {
        _recorder = recorder;
        _labelSources = labelSources;
        _hideWindow = hideWindow;
        _startedByUs = startedByUs;
        Stem = Path.ChangeExtension(wavPath, null);
        _jsonl = Stem + ".transcript.jsonl";
        _live = Stem + ".live.json";
        Append(new { type = "meta", audio = Path.GetFileName(wavPath), started = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), engine = "Windows Live Captions" });
        Append(new { type = "language", language = CaptionLanguage(), probability = 1.0 });
        WriteLive(force: true);
        _thread = new Thread(Run) { IsBackground = true, Name = "live-captions" };
        _thread.Start();
    }

    /// <summary>Starts Live Captions if it is not running. Returns false if it is not available.</summary>
    public static bool EnsureRunning(out bool started)
    {
        started = false;
        if (Process.GetProcessesByName("LiveCaptions").Length > 0) return true;
        var exe = Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe");
        if (!File.Exists(exe)) return false;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose(); started = true; return true; }
        catch { return false; }
    }

    public static bool EnsureRunning() => EnsureRunning(out _);

    private void HideLiveCaptionsWindow(AutomationElement win)
    {
        if (!_hideWindow || _hiddenByUs) return;
        _lcWindow = new IntPtr(win.Current.NativeWindowHandle);
        if (_lcWindow != IntPtr.Zero && IsWindowVisible(_lcWindow))
        {
            ShowWindow(_lcWindow, 0 /* SW_HIDE */);
            _hiddenByUs = true;
        }
    }

    private void RestoreLiveCaptionsWindow()
    {
        if (_lcWindow == IntPtr.Zero) return;
        if (_startedByUs) PostMessageW(_lcWindow, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        else if (_hiddenByUs) ShowWindow(_lcWindow, 4 /* SW_SHOWNOACTIVATE */);
        _hiddenByUs = false;
    }

    private static string CaptionLanguage()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\LiveCaptions\UI");
        var tag = k?.GetValue("CaptionLanguage") as string ?? "en-US";
        return tag.Split('-')[0];
    }

    /// <summary>Commits what is left and finishes the transcript files.</summary>
    public void Stop()
    {
        if (_stopping) return;
        Thread.Sleep(900);   // Live Captions shows the last words ~0.5-1 s after they are spoken
        _stopping = true;
        _thread.Join(2000);
        if (Monitor.TryEnter(_gate, 3000))
        {
            try { if (!_paused) Poll(final: true); }   // paused: already committed
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            finally { Monitor.Exit(_gate); }
        }
        else MainWindow.Log("live captions: final read skipped (caption reader busy)");
        Append(new { type = "done", duration = Math.Round(Math.Max(_recorder.Elapsed.TotalSeconds, _lastElapsed), 2) });
        RestoreLiveCaptionsWindow();
        MainWindow.Log($"live captions: stopped, {_committedCount} segments saved");
        try { File.Delete(_live); } catch { }
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    /// <summary>Pause: keep what was said so far; ignore captions until Resume.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            try { Poll(final: true); } catch (ElementNotAvailableException) { } catch (System.Runtime.InteropServices.COMException) { }
            _paused = true;
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_gate)
        {
            _baselineTaken = false;   // text that appeared while paused is skipped
            _paused = false;
        }
    }

    // ------------------------------------------------------------------ polling

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        double nextFind = 0;
        while (!_stopping)
        {
            var (pc, mic) = _recorder.Levels;
            _lastElapsed = Math.Max(_lastElapsed, _recorder.Elapsed.TotalSeconds);
            lock (_levels)
            {
                _levels.Add((_recorder.Elapsed.TotalSeconds, pc, mic));
                if (_levels.Count > 20 * 60 * 30) _levels.RemoveRange(0, _levels.Count - 20 * 60 * 20);
            }

            if (_textBlock == null && sw.Elapsed.TotalSeconds >= nextFind)
            {
                nextFind = sw.Elapsed.TotalSeconds + 1;
                FindTextBlock();
            }
            if (_textBlock != null)
            {
                try { lock (_gate) if (!_paused) Poll(final: false); }
                catch (ElementNotAvailableException) { _textBlock = null; }
                catch (System.Runtime.InteropServices.COMException) { _textBlock = null; }
            }
            if ((DateTime.UtcNow - _lastLiveWrite).TotalSeconds > 2) WriteLive(force: true);  // heartbeat
            Thread.Sleep(PollMs);
        }
    }

    private void FindTextBlock()
    {
        try
        {
            var procs = Process.GetProcessesByName("LiveCaptions");
            if (procs.Length == 0)
            {
                EnsureRunning();
                SetStatus("loading", "Starting Live Captions…");
                return;
            }
            // A window we hid is not in the UI Automation tree: show it (without
            // activating) to look the element up again, then hide it once more.
            if (_hiddenByUs && _lcWindow != IntPtr.Zero)
            {
                ShowWindow(_lcWindow, 4 /* SW_SHOWNOACTIVATE */);
                _hiddenByUs = false;
            }
            var win = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, procs[0].Id));
            foreach (var p in procs) p.Dispose();
            if (win == null) return;
            _textBlock = win.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"));
            MainWindow.Log($"live captions: window found, caption text {(_textBlock != null ? "found" : "not shown yet")}");
            // The caption element only exists once Live Captions has heard speech, so
            // the window is hidden when the element is found (it keeps captioning).
            if (_textBlock != null) HideLiveCaptionsWindow(win);
            if (_textBlock == null)
                SetStatus("listening", "Listening");   // Live Captions shows its "ready" text until speech is heard
        }
        catch (ElementNotAvailableException) { _textBlock = null; }
    }

    private void Poll(bool final)
    {
        if (final && !_baselineTaken) return;
        string text = _textBlock?.Current.Name ?? "";
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        double now = _recorder.Elapsed.TotalSeconds;

        if (!_baselineTaken)
        {
            // Text already on screen belongs to before this recording.
            _baselineTaken = true;
            if (lines.Count > 0)
            {
                _lastCommitted = lines.Count >= 2 ? lines[^2] : null;
                _lastCommittedIndex = lines.Count - 2;
                _skipPrefix = lines[^1];
                _activeLine = lines[^1];
            }
            SetStatus("listening", "Listening");
            return;
        }

        // Lines after the last committed one are new; all but the last are finished.
        // The anchor is found by text, but only at or before the line position where it was
        // (old lines can scroll off the top; nothing is inserted above the end). Searching
        // the whole text would match a later, identical paragraph ("Thank you.") and skip it.
        int start = 0;
        if (_lastCommitted != null)
        {
            int from = Math.Min(_lastCommittedIndex, lines.Count - 1), j = -1;
            for (int k = from; k >= 0 && j < 0; k--)
                if (lines[k] == _lastCommitted) j = k;
            if (j < 0)
            {
                // Revised slightly? Match on the first words; else keep the old position.
                var head = _lastCommitted[..Math.Min(24, _lastCommitted.Length)];
                for (int k = from; k >= 0 && j < 0; k--)
                    if (lines[k].StartsWith(head, StringComparison.Ordinal)) j = k;
                if (j < 0) j = Math.Min(_lastCommittedIndex, lines.Count - 2);
            }
            start = j + 1;
            _lastCommittedIndex = j;
        }
        var pending = lines.Skip(start).ToList();
        if (final)
            MainWindow.Log($"live captions: final read lines={lines.Count} start={start} pending={pending.Count} " +
                           $"active={_activeLine.Length} chars skipPrefix={_skipPrefix?.Length ?? -1}");
        if (pending.Count == 0)
        {
            // Final read came back empty (caption text cleared or unreadable): keep what was on screen.
            if (final && _activeLine.Length > 0)
            {
                CommitLine(_activeLine, new List<double>(_activeWordTimes));
                _lastCommitted = _activeLine;
                _activeLine = "";
                _activeWordTimes.Clear();
            }
            else UpdateActive("", now);
            return;
        }

        bool changed = false;
        int finishedCount = final ? pending.Count : pending.Count - 1;
        for (int i = 0; i < finishedCount; i++)
        {
            var line = pending[i];
            var times = i == 0 && SameParagraph(line, _activeLine) ? Times(line, now) : Enumerable.Repeat(now, Words(line).Length).ToList();
            CommitLine(line, times);
            _lastCommitted = line;
            _lastCommittedIndex = start + i;
            _activeLine = "";
            _activeWordTimes.Clear();
            changed = true;
        }
        if (!final) changed |= UpdateActive(pending[^1], now);
        if (changed) { WriteLive(force: false); Changed?.Invoke(); }
    }

    private static bool SameParagraph(string a, string b) =>
        b.Length > 0 && (a.StartsWith(b[..Math.Min(b.Length, 12)], StringComparison.Ordinal));

    private static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>First-seen time of each word of the active line (by position).</summary>
    private List<double> Times(string line, double now)
    {
        var words = Words(line);
        var t = new List<double>(words.Length);
        for (int i = 0; i < words.Length; i++)
            t.Add(i < _activeWordTimes.Count ? _activeWordTimes[i] : now);
        return t;
    }

    private bool UpdateActive(string line, double now)
    {
        if (line == _activeLine) return false;
        if (!SameParagraph(line, _activeLine)) _activeWordTimes.Clear();
        var t = Times(line, now);
        _activeWordTimes.Clear();
        _activeWordTimes.AddRange(t);
        _activeLine = line;
        return true;
    }

    // ------------------------------------------------------------------ sentences + sources

    private static readonly Regex SentenceEnd = new(@"[.?!]$", RegexOptions.Compiled);

    private record Piece(string? Source, string Text, double Start, double End);

    /// <summary>Splits a line into sentences and labels each by the louder source.</summary>
    private List<Piece> Label(string line, List<double> times)
    {
        if (_skipPrefix != null)
        {
            if (line.StartsWith(_skipPrefix, StringComparison.Ordinal))
            {
                int skipWords = Words(_skipPrefix).Length;
                times = times.Skip(skipWords).ToList();
                line = line[_skipPrefix.Length..].Trim();
            }
        }
        var words = Words(line);
        var pieces = new List<Piece>();
        int from = 0;
        for (int i = 0; i < words.Length; i++)
        {
            if (!SentenceEnd.IsMatch(words[i]) && i != words.Length - 1) continue;
            double t0 = from < times.Count ? times[from] : times.LastOrDefault();
            double t1 = i < times.Count ? times[i] : times.LastOrDefault();
            string? src = SourceFor(t0 - AudioLeadSeconds, t1 - AudioTailSeconds);
            var text = string.Join(' ', words[from..(i + 1)]);
            if (pieces.Count > 0 && pieces[^1].Source == src)
                pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + " " + text, End = t1 };
            else
                pieces.Add(new Piece(src, text, t0, t1));
            from = i + 1;
        }
        return pieces;
    }

    private string? SourceFor(double from, double to)
    {
        // Live Captions hears only PC audio and the microphone. Without our own mic level
        // (Record my microphone off), speech while the PC was silent must be the mic.
        if (!_labelSources)
        {
            double sum = 0;
            int count = 0;
            lock (_levels)
                foreach (var (t, p, _) in _levels)
                    if (t >= from && t <= Math.Max(to, from + 0.3)) { sum += p; count++; }
            return count > 0 && sum / count > SpeechLevel ? Sources.Pc : Sources.Mic;
        }
        if (to < from + 0.3) to = from + 0.3;
        double pc = 0, mic = 0;
        int n = 0;
        lock (_levels)
        {
            foreach (var (t, p, m) in _levels)
                if (t >= from && t <= to) { pc += p; mic += m; n++; }
        }
        if (n == 0) return _lastSource ?? Sources.Pc;
        pc /= n; mic /= n;
        string? src = mic > SpeechLevel && mic > pc * 1.2 ? Sources.Mic
                    : pc > SpeechLevel ? Sources.Pc
                    : mic > SpeechLevel ? Sources.Mic
                    : _lastSource ?? Sources.Pc;
        _lastSource = src;
        return src;
    }

    private void CommitLine(string line, List<double> times)
    {
        var labelled = Label(line, times);
        MainWindow.Log($"live captions: commit line ({line.Length} chars) -> {labelled.Count(p => p.Text.Length > 0)} pieces");
        foreach (var p in labelled)
        {
            if (p.Text.Length == 0) continue;
            // Words appear ~0.5-1 s after they are spoken: shift back to audio time.
            double start = Math.Max(0, p.Start - 0.8), end = Math.Max(start, p.End - AudioTailSeconds);
            Append(new { type = "segment", start = Math.Round(start, 2), end = Math.Round(end, 2), text = p.Text, source = p.Source });
            _committedCount++;
        }
        _skipPrefix = null;   // only the line that was on screen at the start has a prefix to skip
    }

    // ------------------------------------------------------------------ files

    private void SetStatus(string status, string message)
    {
        if (_status == status && _message == message) return;
        _status = status;
        _message = message;
        WriteLive(force: true);
        Changed?.Invoke();
    }

    private void WriteLive(bool force)
    {
        var pieces = _activeLine.Length > 0 && _baselineTaken && _activeLine != _skipPrefix
            ? Label(_activeLine, new List<double>(_activeWordTimes)) : new List<Piece>();
        var parts = pieces.Where(p => p.Text.Length > 0).Select(p => new { source = p.Source, text = p.Text }).ToList();
        var partials = new Dictionary<string, string>();
        foreach (var p in parts) partials[p.source ?? "mixed"] = (partials.GetValueOrDefault(p.source ?? "mixed") + " " + p.text).Trim();
        var json = JsonSerializer.Serialize(new
        {
            status = _status, message = _message, partials, parts,
            position = Math.Round(_recorder.Elapsed.TotalSeconds, 2),
            updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        });
        if (!force && json == _lastLiveJson) return;
        _lastLiveJson = json;
        _lastLiveWrite = DateTime.UtcNow;
        try
        {
            var tmp = _live + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _live, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private readonly object _appendLock = new();

    private void Append(object obj)
    {
        lock (_appendLock)
        {
            try { File.AppendAllText(_jsonl, JsonSerializer.Serialize(obj) + "\n"); }
            catch (IOException) { }
        }
    }
}
