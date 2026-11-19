using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RSPAudioRecorder;

public partial class MainWindow : Window
{
    private const int HotkeyId = 1;
    private const double CompactWidth = 580, CompactHeight = 262;

    private readonly Recorder _recorder = new();
    private readonly ObservableCollection<Session> _sessions = new();
    private readonly Dictionary<string, Session> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _ui, _fast, _slow;

    private IntPtr _hwnd;
    private bool _quitting;
    private bool _expanded;
    private Size _expandedSize = new(1060, 740);
    private string _message = "Ready to record";
    private Brush? _messageDot;
    private float _glow;

    private Session? _current;
    private bool _follow;              // keep jumping to the live recording
    private bool _selecting;           // selection change made by code, not the user
    private string _query = "";

    // Incremental rendering state for the current session.
    private int _rendered;
    private double _lastRenderedStart;
    private Paragraph? _lastPara;
    private string? _lastParaSource;
    private double _lastParaStart, _lastParaEnd;
    private readonly List<(Paragraph host, Inline inline, bool ownPara)> _partials = new();

    private static readonly Brush FaintBrush = Frozen("#9AA3B2");
    private static readonly Brush MutedBrush = Frozen("#7B8494");
    private static readonly Brush HighlightBrush = Frozen("#FFE9A8");
    private static readonly Brush LiveBrush = Frozen("#E53935");
    private static readonly Brush LiveSoftBrush = Frozen("#FDECEC");
    private static readonly Brush AmberBrush = Frozen("#B26A00");
    private static readonly Brush AmberSoftBrush = Frozen("#FFF4E0");
    private static readonly Brush GreySoftBrush = Frozen("#EEF1F5");
    private static readonly Brush PausedBrush = Frozen("#F59E0B");
    private static readonly Brush SavedBrush = Frozen("#16A34A");
    private static readonly Brush MicBrush = Frozen("#0A7CFF");
    private static readonly Brush MicSoftBrush = Frozen("#E8F1FF");
    private static readonly Brush PcBrush = Frozen("#7C4DDB");
    private static readonly Brush PcSoftBrush = Frozen("#F1EBFD");

    public MainWindow()
    {
        InitializeComponent();

        var cvs = (CollectionViewSource)Resources["SessionsView"];
        cvs.Source = _sessions;
        _view = (ListCollectionView)cvs.View;
        _view.SortDescriptions.Add(new SortDescription(nameof(Session.Started), ListSortDirection.Descending));
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Session.DayLabel)));
        _view.Filter = o => Matches((Session)o);

        _ui = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) => UiTick(), Dispatcher);
        _fast = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => FastTick(), Dispatcher);
        _slow = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Rescan(), Dispatcher);

        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            // A maximised borderless window overhangs the screen by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M 3,3 H 10 V 10 H 3 Z M 5.5,3 V 0.5 H 13 V 8 H 10"
                : "M 3,0 H 10 A 3,3 0 0 1 13,3 V 10 A 3,3 0 0 1 10,13 H 3 A 3,3 0 0 1 0,10 V 3 A 3,3 0 0 1 3,0 Z");
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ================================================================== background app

    /// <summary>Creates the (hidden) window: tool-window style, rounded corners, global hotkey.</summary>
    public void InitializeBackground()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.MakeToolWindow(this);
        Native.RoundCorners(this);
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        RegisterHotkey();

        ApplyMode(AppSettings.ShowTranscript, initial: true);
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;

        Rescan();
        Select(_sessions.Where(s => s.IsLive).OrderByDescending(s => s.Started).FirstOrDefault()
               ?? _sessions.OrderByDescending(s => s.Started).FirstOrDefault());
        _ui.Start();
        _fast.Start();
        _slow.Start();
        UpdateRecorderUi();
    }

    private bool RegisterHotkey()
    {
        bool ok = Native.RegisterHotkey(_hwnd, HotkeyId, AppSettings.Hotkey);
        Log($"hotkey {Native.HotkeyText(AppSettings.Hotkey)} registered={ok} hwnd={_hwnd}");
        return ok;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            Log($"WM_HOTKEY id={wParam} visible={IsVisible} active={IsActive}");
            if (wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Toggle();
            }
        }
        return IntPtr.Zero;
    }

    internal static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RSP-Audio-Recorder");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    private void Toggle()
    {
        if (OwnedWindows.Count > 0) { OwnedWindows[0].Activate(); return; }
        // WPF's IsActive can be stale for a hidden tool window; ask Windows.
        if (IsVisible && Native.GetForegroundWindow() == _hwnd) Hide();
        else ShowFront();
    }

    public void ShowFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Native.SetForegroundWindow(_hwnd);
        Focus();
        if (_expanded) Rescan();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;   // closing only hides; the app keeps listening for the shortcut
        Hide();
    }

    public void QuitApp()
    {
        if (_quitting) return;
        _quitting = true;
        _recorder.StopNow();
        Native.UnregisterHotkey(_hwnd, HotkeyId);
        Close();
        Application.Current.Shutdown();
    }

    public void SaveAndStop() => _recorder.StopNow();

    private void Min_Click(object sender, RoutedEventArgs e) => Hide();
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBoxBase;
        if (e.Key == Key.Escape)
        {
            if (typing && SearchBox.Text.Length > 0) SearchBox.Text = "";
            else Hide();
            e.Handled = true;
        }
        else if (!typing && e.Key == Key.Space) { Main_Click(this, e); e.Handled = true; }
        else if (!typing && e.Key == Key.S && StopButton.IsEnabled) { Stop_Click(this, e); e.Handled = true; }
    }

    // ================================================================== compact / expanded

    private void TranscriptToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyMode(!_expanded, initial: false);
        AppSettings.ShowTranscript = _expanded;
    }

    private void ApplyMode(bool expanded, bool initial)
    {
        if (!initial && _expanded && WindowState == WindowState.Normal) _expandedSize = new Size(Width, Height);
        if (WindowState == WindowState.Maximized && !expanded) WindowState = WindowState.Normal;
        _expanded = expanded;

        double centerX = Left + Width / 2;
        Panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        MaxButton.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        // Compact size is pinned with min = max (switching ResizeMode on a WindowChrome
        // window makes WPF restore the previous size).
        if (expanded)
        {
            MaxWidth = MaxHeight = double.PositiveInfinity;
            MinWidth = 760;
            MinHeight = 520;
        }
        else
        {
            MinWidth = MaxWidth = CompactWidth;
            MinHeight = MaxHeight = CompactHeight;
        }
        Width = expanded ? _expandedSize.Width : CompactWidth;
        Height = expanded ? _expandedSize.Height : CompactHeight;
        if (!initial)
        {
            // Keep the window on screen, centred where it was.
            var wa = SystemParameters.WorkArea;
            Left = Math.Max(wa.Left, Math.Min(centerX - Width / 2, wa.Right - Width));
            Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
        }
        if (expanded) { Rescan(); Render(full: true); }
    }

    // ================================================================== recorder

    private void Main_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.State == Recorder.RecState.Idle)
        {
            var error = _recorder.Start(AppSettings.FileName, AppSettings.RecordMic, AppSettings.LiveTranscript);
            if (error != null) { SetMessage(error, LiveBrush); return; }
            SetMessage(AppSettings.LiveTranscript && !_recorder.TranscriberRunning
                ? "Recording (transcript unavailable)" : "Recording", LiveBrush);
            _follow = true;
            Rescan();
        }
        else
        {
            _recorder.TogglePause();
            SetMessage(_recorder.State == Recorder.RecState.Paused ? "Paused" : "Recording",
                       _recorder.State == Recorder.RecState.Paused ? PausedBrush : LiveBrush);
        }
        UpdateRecorderUi();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.State == Recorder.RecState.Idle) return;
        StopButton.IsEnabled = false;
        SetMessage("Saving…", PausedBrush);
        var saved = await _recorder.StopAsync();
        if (saved != null)
        {
            SetMessage("Saved  ·  " + Path.GetFileName(saved), SavedBrush);
            if (AppSettings.OpenAfterSave && File.Exists(saved))
                Process.Start(new ProcessStartInfo(saved) { UseShellExecute = true });
        }
        UpdateRecorderUi();
    }

    private void PlayLast_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.LastSaved is { } f && File.Exists(f))
            Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
    }

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        // The shortcut picker must receive the current shortcut instead of it toggling the window.
        Native.UnregisterHotkey(_hwnd, HotkeyId);
        var dlg = new SettingsWindow(_hwnd, HotkeyId) { Owner = this };
        bool? ok = dlg.ShowDialog();
        RegisterHotkey();
        if (dlg.QuitRequested) QuitApp();
        else if (ok == true) UpdateRecorderUi();
    }

    private void SetMessage(string text, Brush? dot)
    {
        _message = text;
        _messageDot = dot;
        UpdateRecorderUi();
    }

    private void UpdateRecorderUi()
    {
        var st = _recorder.State;
        RecordGlyph.Visibility = st == Recorder.RecState.Idle ? Visibility.Visible : Visibility.Collapsed;
        PauseGlyph.Visibility = st == Recorder.RecState.Recording ? Visibility.Visible : Visibility.Collapsed;
        ResumeGlyph.Visibility = st == Recorder.RecState.Paused ? Visibility.Visible : Visibility.Collapsed;
        MainButton.ToolTip = st switch
        {
            Recorder.RecState.Idle => "Record (Space)",
            Recorder.RecState.Recording => "Pause (Space)",
            _ => "Resume (Space)",
        };
        StopButton.IsEnabled = st != Recorder.RecState.Idle;
        PlayButton.IsEnabled = st == Recorder.RecState.Idle && _recorder.LastSaved != null && File.Exists(_recorder.LastSaved);

        string text = _message;
        if (st != Recorder.RecState.Idle) text += "  ·  " + Session.FormatTime(_recorder.Elapsed.TotalSeconds);
        StatusText.Text = text;
        StatusDot.Visibility = _messageDot == null ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.Fill = _messageDot;
        if (st == Recorder.RecState.Recording)
            StatusDot.Opacity = DateTime.Now.Millisecond < 600 ? 1 : 0.25;   // blink while recording
        else StatusDot.Opacity = 1;
    }

    private void UiTick()
    {
        if (_recorder.ExitedUnexpectedly(out int code))
        {
            if (code == 0) SetMessage("Saved  ·  " + Path.GetFileName(_recorder.LastSaved), SavedBrush);
            else SetMessage("Recording stopped unexpectedly", LiveBrush);
        }
        if (!IsVisible) return;
        _glow = Math.Max(_recorder.Level, _glow * 0.86f);
        double level = _recorder.State == Recorder.RecState.Recording ? Math.Min(1, _glow * 2.5) : 0;
        Glow.Opacity = 0.55 + 0.45 * level;
        Glow.RenderTransformOrigin = new Point(0.5, 0.5);
        Glow.RenderTransform = new ScaleTransform(1 + 0.08 * level, 1 + 0.08 * level);
        if (_recorder.State != Recorder.RecState.Idle || StopButton.IsEnabled) UpdateRecorderUi();
    }

    // ================================================================== transcripts: data

    private void Rescan()
    {
        var found = new HashSet<string>(TranscriptStore.Scan(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in found.Where(p => !_byPath.ContainsKey(p)))
        {
            var s = new Session(path);
            s.Refresh();
            _byPath[path] = s;
            _sessions.Add(s);
        }
        foreach (var gone in _byPath.Keys.Where(p => !found.Contains(p)).ToList())
        {
            var s = _byPath[gone];
            _byPath.Remove(gone);
            _sessions.Remove(s);
            if (s == _current) Select(null);
        }
        foreach (var s in _sessions.Where(s => !s.Done && s != _current))
            s.Refresh();

        var newestLive = _sessions.Where(s => s.IsLive).OrderByDescending(s => s.Started).FirstOrDefault();
        if (_follow && newestLive != null && newestLive != _current) Select(newestLive);
        else if (_current == null && _query.Length == 0 && _sessions.Count > 0)
            Select(newestLive ?? _sessions.OrderByDescending(s => s.Started).First());
        UpdateEmptyStates();
    }

    private void FastTick()
    {
        if (_current == null || !_expanded || !IsVisible) return;
        if (_current.Done && !_current.IsLive && _partials.Count == 0) return;
        if (_current.Refresh()) Render(full: false);
    }

    private bool Matches(Session s) =>
        _query.Length == 0 || s.FullText.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
                           || s.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase);

    private void Select(Session? s)
    {
        _selecting = true;
        SessionList.SelectedItem = s;
        _selecting = false;
        if (s != null) SessionList.ScrollIntoView(s);
        Open(s);
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting) return;
        var s = SessionList.SelectedItem as Session;
        // Choosing an older transcript stops auto-following; choosing the live one resumes it.
        _follow = s?.IsLive == true;
        Open(s);
    }

    private void Open(Session? s)
    {
        if (s == _current && s != null) return;
        _current = s;
        s?.Refresh();
        Render(full: true);
    }

    // ================================================================== transcripts: rendering

    private void Render(bool full)
    {
        var s = _current;
        UpdateHeader();
        if (s == null || !_expanded)
        {
            if (s == null) { Doc.Document.Blocks.Clear(); ResetRenderState(); }
            return;
        }

        // Lines from the two sources are committed independently, so one can arrive
        // that belongs before text already shown: then redraw in time order.
        if (!full && s.Segments.Skip(_rendered).Any(g => g.Start < _lastRenderedStart - 0.01)) full = true;

        bool atBottom = full ? s.IsLive || IsAtBottom() : IsAtBottom();
        double offset = Doc.VerticalOffset;
        if (full)
        {
            Doc.Document.Blocks.Clear();
            ResetRenderState();
        }

        RemovePartials();
        var pending = full ? s.Segments.OrderBy(g => g.Start).ToList() : s.Segments.Skip(_rendered).ToList();
        foreach (var seg in pending)
        {
            AddSegment(seg);
            _lastRenderedStart = Math.Max(_lastRenderedStart, seg.Start);
        }
        _rendered = s.Segments.Count;
        if (s.IsLive) AddPartials(s);

        if (Doc.Document.Blocks.Count == 0)
            Doc.Document.Blocks.Add(new Paragraph(new Run(EmptyDocText(s))) { Foreground = MutedBrush, Margin = new Thickness(0) });

        if (atBottom) Doc.ScrollToEnd();
        else if (full && _query.Length > 0) { Doc.ScrollToHome(); ScrollToFirstHit(); }
        else if (full) Doc.ScrollToVerticalOffset(offset);
        UpdateJumpLive();
    }

    private static string EmptyDocText(Session s)
    {
        if (s.Error != null) return "Transcription failed: " + s.Error;
        if (s.IsLive) return s.LiveStatus == "loading" ? "Loading the speech model…" : "Listening… text appears here as people speak.";
        return "No speech was detected in this recording.";
    }

    private void ResetRenderState()
    {
        _rendered = 0;
        _lastRenderedStart = 0;
        _lastPara = null;
        _lastParaSource = null;
        _partials.Clear();
    }

    private bool IsPlaceholder => _rendered == 0 && _lastPara == null && _partials.Count == 0;

    /// <summary>Paragraph header: source label (Microphone / PC audio) and timestamp, in one row.</summary>
    private static InlineUIContainer ParaHeader(string? source, string stamp, Brush stampBrush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        if (!string.IsNullOrEmpty(Sources.Label(source)))
        {
            bool mic = source == Sources.Mic;
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 1, 7, 2),
                Background = mic ? MicSoftBrush : PcSoftBrush,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Sources.Label(source),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = mic ? MicBrush : PcBrush,
                },
            });
        }
        row.Children.Add(new TextBlock
        {
            Text = stamp, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = stampBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new InlineUIContainer(row) { BaselineAlignment = BaselineAlignment.Top };
    }

    private Paragraph NewParagraph(string? source, string stamp, Brush stampBrush)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 18) };
        p.Inlines.Add(ParaHeader(source, stamp, stampBrush));
        p.Inlines.Add(new LineBreak());
        return p;
    }

    private void AddSegment(Segment seg)
    {
        if (_rendered == 0 && _lastPara == null) Doc.Document.Blocks.Clear();   // drop the placeholder
        bool joins = _lastPara != null && seg.Source == _lastParaSource
                     && seg.Start - _lastParaEnd < 2.5 && seg.End - _lastParaStart < 60;
        if (joins)
        {
            AddText(_lastPara!, " " + seg.Text);
            _lastParaEnd = Math.Max(_lastParaEnd, seg.End);
            return;
        }
        var p = NewParagraph(seg.Source, Session.FormatTime(seg.Start), FaintBrush);
        AddText(p, seg.Text);
        Doc.Document.Blocks.Add(p);
        _lastPara = p;
        _lastParaSource = seg.Source;
        _lastParaStart = seg.Start;
        _lastParaEnd = seg.End;
    }

    /// <summary>Adds text, highlighting the current search term.</summary>
    private void AddText(Paragraph p, string text)
    {
        if (_query.Length == 0) { p.Inlines.Add(new Run(text)); return; }
        int i = 0;
        while (i < text.Length)
        {
            int hit = text.IndexOf(_query, i, StringComparison.CurrentCultureIgnoreCase);
            if (hit < 0) { p.Inlines.Add(new Run(text[i..])); break; }
            if (hit > i) p.Inlines.Add(new Run(text[i..hit]));
            p.Inlines.Add(new Run(text.Substring(hit, _query.Length)) { Background = HighlightBrush, Tag = "hit" });
            i = hit + _query.Length;
        }
    }

    /// <summary>The words still being worked out, in grey: one live line per source.</summary>
    private void AddPartials(Session s)
    {
        foreach (var (key, text) in s.Partials.OrderBy(p => p.Key == Sources.Pc ? 0 : 1))
        {
            if (text.Length == 0) continue;
            string? source = key is Sources.Mic or Sources.Pc ? key : null;
            if (_rendered == 0 && _lastPara == null && _partials.Count == 0) Doc.Document.Blocks.Clear();
            var run = new Run(text) { Foreground = MutedBrush };
            if (_lastPara != null && _lastParaSource == source && s.Duration - _lastParaEnd < 6)
            {
                run.Text = " " + text;
                _lastPara.Inlines.Add(run);
                _partials.Add((_lastPara, run, false));
            }
            else
            {
                var p = NewParagraph(source, "now", LiveBrush);
                p.Inlines.Add(run);
                Doc.Document.Blocks.Add(p);
                _partials.Add((p, run, true));
            }
        }
    }

    private void RemovePartials()
    {
        foreach (var (host, inline, own) in _partials)
        {
            if (own) Doc.Document.Blocks.Remove(host);
            else host.Inlines.Remove(inline);
        }
        _partials.Clear();
        if (_rendered == 0 && _lastPara == null) Doc.Document.Blocks.Clear();
    }

    private void UpdateHeader()
    {
        var s = _current;
        bool has = s != null;
        MainEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Doc.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Header.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        if (s == null) { JumpLive.Visibility = Visibility.Collapsed; return; }

        TitleText.Text = s.Title;
        var parts = new List<string> { s.DurationText };
        int words = s.WordCount;
        parts.Add(words == 1 ? "1 word" : $"{words:N0} words");
        if (!string.IsNullOrEmpty(s.Language))
        {
            try { parts.Add(CultureInfo.GetCultureInfo(s.Language).EnglishName); } catch (CultureNotFoundException) { }
        }
        parts.Add(Path.GetFileName(s.WavPath));
        SubtitleText.Text = string.Join("  ·  ", parts);

        if (s.IsLive)
        {
            bool loading = s.LiveStatus == "loading", finishing = s.LiveStatus == "finishing";
            SetChip(loading ? "Loading model" : finishing ? "Finishing" : "Live",
                    loading || finishing ? AmberBrush : LiveBrush, loading || finishing ? AmberSoftBrush : LiveSoftBrush);
        }
        else if (s.Error != null) SetChip("Transcription failed", LiveBrush, GreySoftBrush);
        else StatusChip.Visibility = Visibility.Collapsed;

        bool audio = File.Exists(s.WavPath);
        PlaySessionButton.IsEnabled = audio && !s.IsLive;
        FolderButton.IsEnabled = audio || File.Exists(s.JsonlPath);
        CopyButton.IsEnabled = ExportButton.IsEnabled = s.Segments.Count > 0;
    }

    private void SetChip(string text, Brush fg, Brush bg)
    {
        StatusChip.Visibility = Visibility.Visible;
        StatusChip.Background = bg;
        ChipDot.Fill = fg;
        ChipText.Foreground = fg;
        ChipText.Text = text;
    }

    private bool IsAtBottom() => Doc.VerticalOffset + Doc.ViewportHeight >= Doc.ExtentHeight - 40;

    private void UpdateJumpLive() =>
        JumpLive.Visibility = _current?.IsLive == true && !IsAtBottom() ? Visibility.Visible : Visibility.Collapsed;

    private void Doc_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateJumpLive();

    private void JumpLive_Click(object sender, RoutedEventArgs e)
    {
        Doc.ScrollToEnd();
        _follow = true;
    }

    private void ScrollToFirstHit()
    {
        foreach (var block in Doc.Document.Blocks.OfType<Paragraph>())
            foreach (var run in block.Inlines.OfType<Run>())
                if (Equals(run.Tag, "hit"))
                {
                    Dispatcher.BeginInvoke(() => block.BringIntoView(), DispatcherPriority.Loaded);
                    return;
                }
    }

    private void UpdateEmptyStates()
    {
        if (_sessions.Count == 0)
        {
            SidebarEmpty.Text = "Your recordings and their transcripts appear here.";
            SidebarEmpty.Visibility = Visibility.Visible;
        }
        else if (_query.Length > 0 && _view.Count == 0)
        {
            SidebarEmpty.Text = $"No transcripts mention “{_query}”.";
            SidebarEmpty.Visibility = Visibility.Visible;
        }
        else SidebarEmpty.Visibility = Visibility.Collapsed;
    }

    // ================================================================== search

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var keep = _current;
        _selecting = true;
        _view.Refresh();
        _selecting = false;
        if (keep != null && !Matches(keep))
            Select(_view.Cast<Session>().FirstOrDefault());
        else
        {
            _selecting = true;
            SessionList.SelectedItem = keep;
            _selecting = false;
            Render(full: true);
        }
        UpdateEmptyStates();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    // ================================================================== transcript actions

    private static string PlainText(Session s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(s.Title);
        sb.AppendLine();
        string? source = null;
        double paraStart = -1, paraEnd = -100;
        var para = new StringBuilder();
        void Flush()
        {
            if (para.Length == 0) return;
            sb.Append('[').Append(Session.FormatTime(paraStart)).Append("] ");
            var label = Sources.Label(source);
            if (label.Length > 0) sb.Append(label).Append(": ");
            sb.AppendLine(para.ToString().Trim());
            sb.AppendLine();
            para.Clear();
        }
        foreach (var seg in s.Segments.OrderBy(g => g.Start))
        {
            if (seg.Source != source || seg.Start - paraEnd >= 2.5 || seg.End - paraStart >= 60)
            {
                Flush();
                paraStart = seg.Start;
                source = seg.Source;
            }
            para.Append(seg.Text).Append(' ');
            paraEnd = seg.End;
        }
        Flush();
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try { Clipboard.SetText(PlainText(_current)); }
        catch (COMException) { return; }  // clipboard busy
        CopyLabel.Text = "Copied";
        await Task.Delay(1500);
        CopyLabel.Text = "Copy";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(_current.Stem) + ".txt",
            InitialDirectory = AppSettings.OutputFolder,
            Filter = "Text file (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, PlainText(_current), Encoding.UTF8);
    }

    private void PlaySession_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null && File.Exists(_current.WavPath))
            Process.Start(new ProcessStartInfo(_current.WavPath) { UseShellExecute = true });
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var target = File.Exists(_current.WavPath) ? _current.WavPath : _current.JsonlPath;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
