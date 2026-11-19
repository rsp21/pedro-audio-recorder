using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace RSPAudioRecorder;

/// <summary>Where a line was heard: the microphone, PC audio, or (older recordings) unknown.</summary>
public static class Sources
{
    public const string Mic = "mic";
    public const string Pc = "pc";

    public static string Label(string? source) => source switch
    {
        Mic => "Microphone",
        Pc => "PC audio",
        _ => "",
    };
}

public sealed record Segment(double Start, double End, string Text, string? Source);

/// <summary>
/// One recording's transcript: &lt;name&gt;.transcript.jsonl (append-only, written by
/// live_transcriber.py) plus &lt;name&gt;.live.json while the recording is running.
/// </summary>
public sealed class Session : INotifyPropertyChanged
{
    public string JsonlPath { get; }
    public string Stem { get; }
    public string WavPath => Stem + ".wav";
    public string LivePath => Stem + ".live.json";
    public List<Segment> Segments { get; } = new();
    public DateTime Started { get; private set; }
    public bool Done { get; private set; }
    public string? Error { get; private set; }
    public string? Language { get; private set; }

    // Live state (from .live.json)
    public string LiveStatus { get; private set; } = "";
    public string LiveMessage { get; private set; } = "";
    public Dictionary<string, string> Partials { get; private set; } = new();
    /// <summary>The live text in order, as (source, text) pieces (Live Captions engine).</summary>
    public List<(string? Source, string Text)> Parts { get; private set; } = new();
    private double _position;
    private double _finalDuration;
    private long _offset;
    private string _carry = "";
    private bool _isLive;

    public Session(string jsonlPath)
    {
        JsonlPath = jsonlPath;
        Stem = jsonlPath[..^".transcript.jsonl".Length];
        Started = GuessStart();
    }

    public bool IsLive
    {
        get => _isLive;
        private set { if (_isLive != value) { _isLive = value; Raise(); } }
    }

    public bool HasPartial => Partials.Values.Any(p => p.Length > 0);

    public double Duration => _finalDuration > 0 ? _finalDuration
        : Math.Max(_position, Segments.Count > 0 ? Segments.Max(s => s.End) : 0);

    public string DurationText => FormatTime(Duration);
    public string TimeText => Started.ToString("h:mm tt", CultureInfo.CurrentCulture);
    public string DayLabel
    {
        get
        {
            var d = Started.Date;
            if (d == DateTime.Today) return "Today";
            if (d == DateTime.Today.AddDays(-1)) return "Yesterday";
            return d.Year == DateTime.Today.Year ? d.ToString("dddd, MMMM d") : d.ToString("MMMM d, yyyy");
        }
    }
    public string Title => Started.ToString("dddd, MMMM d · h:mm tt", CultureInfo.CurrentCulture);
    public int WordCount => Segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    public string Preview
    {
        get
        {
            if (Error != null && Segments.Count == 0) return "Transcription failed: " + Error;
            if (Segments.Count == 0)
            {
                var p = string.Join(" ", Partials.Values.Where(v => v.Length > 0));
                return IsLive ? (p.Length > 0 ? p : LiveMessage.Length > 0 ? LiveMessage + "…" : "Listening…")
                              : "No speech detected";
            }
            var sb = new StringBuilder();
            foreach (var s in Segments.OrderBy(s => s.Start)) { sb.Append(s.Text).Append(' '); if (sb.Length > 160) break; }
            return sb.ToString().Trim();
        }
    }

    public string FullText => string.Join(" ", Segments.Select(s => s.Text));

    /// <summary>Reads anything appended since the last call. Returns true if something changed.</summary>
    public bool Refresh()
    {
        bool changed = false;
        try
        {
            using var fs = new FileStream(JsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > _offset)
            {
                fs.Seek(_offset, SeekOrigin.Begin);
                var bytes = new byte[fs.Length - _offset];
                int n = fs.Read(bytes, 0, bytes.Length);
                _offset += n;
                var text = _carry + Encoding.UTF8.GetString(bytes, 0, n);
                int last = text.LastIndexOf('\n');
                _carry = last >= 0 ? text[(last + 1)..] : text;
                if (last >= 0)
                    foreach (var line in text[..last].Split('\n'))
                        changed |= Apply(line.Trim());
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        changed |= RefreshLive();
        if (changed) RaiseAll();
        return changed;
    }

    private bool Apply(string line)
    {
        if (line.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            switch (r.GetProperty("type").GetString())
            {
                case "meta":
                    if (r.TryGetProperty("started", out var st) && DateTime.TryParse(st.GetString(), out var dt))
                        Started = dt;
                    return true;
                case "language":
                    Language = r.GetProperty("language").GetString();
                    return true;
                case "segment":
                    string? source = r.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String
                        ? src.GetString() : null;
                    Segments.Add(new Segment(r.GetProperty("start").GetDouble(), r.GetProperty("end").GetDouble(),
                                             r.GetProperty("text").GetString() ?? "", source));
                    return true;
                case "done":
                    Done = true;
                    _finalDuration = r.GetProperty("duration").GetDouble();
                    return true;
                case "error":
                    Error = r.GetProperty("message").GetString();
                    return true;
            }
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        catch (InvalidOperationException) { }
        return false;
    }

    private bool RefreshLive()
    {
        bool live = false;
        string status = "", message = "";
        var partials = new Dictionary<string, string>();
        var parts = new List<(string? Source, string Text)>();
        if (!Done && File.Exists(LivePath))
        {
            try
            {
                using var fs = new FileStream(LivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                var r = doc.RootElement;
                status = r.GetProperty("status").GetString() ?? "";
                message = r.GetProperty("message").GetString() ?? "";
                if (r.TryGetProperty("partials", out var ps) && ps.ValueKind == JsonValueKind.Object)
                    foreach (var p in ps.EnumerateObject())
                        partials[p.Name] = p.Value.GetString() ?? "";
                else if (r.TryGetProperty("partial", out var single))
                    partials["mixed"] = single.GetString() ?? "";
                if (r.TryGetProperty("parts", out var pa) && pa.ValueKind == JsonValueKind.Array)
                    foreach (var p in pa.EnumerateArray())
                        parts.Add((p.TryGetProperty("source", out var ps2) && ps2.ValueKind == JsonValueKind.String ? ps2.GetString() : null,
                                   p.GetProperty("text").GetString() ?? ""));
                _position = r.GetProperty("position").GetDouble();
                double updated = r.GetProperty("updated").GetDouble();
                // A worker that stopped updating (crashed) is no longer live.
                live = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - updated < 15;
            }
            catch (IOException) { return false; }          // being replaced; keep previous state
            catch (UnauthorizedAccessException) { return false; }
            catch (JsonException) { return false; }
            catch (KeyNotFoundException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        if (!live) { partials.Clear(); parts.Clear(); }
        bool changed = live != IsLive || status != LiveStatus || message != LiveMessage
                       || !partials.OrderBy(k => k.Key).SequenceEqual(Partials.OrderBy(k => k.Key))
                       || !parts.SequenceEqual(Parts);
        IsLive = live;
        LiveStatus = status;
        LiveMessage = message;
        Partials = partials;
        Parts = parts;
        return changed;
    }

    private DateTime GuessStart()
    {
        // audio-20261119-141400
        var name = Path.GetFileName(Stem);
        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (digits.Length >= 14 && DateTime.TryParseExact(digits[..14], "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        try { return File.GetCreationTime(JsonlPath); } catch { return DateTime.Now; }
    }

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public static class TranscriptStore
{
    public static IEnumerable<string> Scan()
    {
        var folder = AppSettings.OutputFolder;
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        try { return Directory.EnumerateFiles(folder, "*.transcript.jsonl").ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
    }
}
