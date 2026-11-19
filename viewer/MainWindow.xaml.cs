using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RSPTranscripts;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Session> _sessions = new();
    private readonly Dictionary<string, Session> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _fast, _slow;

    private Session? _current;
    private bool _follow;              // keep jumping to the newest live recording
    private bool _selecting;           // selection change made by code, not the user
    private string _query = "";

    // Incremental rendering state for the current session.
    private int _rendered;
    private Paragraph? _lastPara;
    private double _lastParaStart, _lastParaEnd;
    private Run? _partialRun;
    private Paragraph? _partialPara;

    private static readonly Brush FaintBrush = Frozen("#9AA3B2");
    private static readonly Brush MutedBrush = Frozen("#7B8494");
    private static readonly Brush HighlightBrush = Frozen("#FFE9A8");
    private static readonly Brush LiveBrush = Frozen("#E53935");
    private static readonly Brush LiveSoftBrush = Frozen("#FDECEC");
    private static readonly Brush AmberBrush = Frozen("#B26A00");
    private static readonly Brush AmberSoftBrush = Frozen("#FFF4E0");
    private static readonly Brush GreySoftBrush = Frozen("#EEF1F5");

    public MainWindow(bool follow)
    {
        InitializeComponent();
        _follow = follow;

        var cvs = (CollectionViewSource)Resources["SessionsView"];
        cvs.Source = _sessions;
        _view = (ListCollectionView)cvs.View;
        _view.SortDescriptions.Add(new SortDescription(nameof(Session.Started), ListSortDirection.Descending));
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Session.DayLabel)));
        _view.Filter = o => Matches((Session)o);

        _fast = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => FastTick(), Dispatcher);
        _slow = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Rescan(), Dispatcher);

        SourceInitialized += (_, _) => TintTitleBar();
        Loaded += (_, _) =>
        {
            Rescan();
            var first = _sessions.Where(s => s.IsLive).OrderByDescending(s => s.Started).FirstOrDefault()
                        ?? _sessions.OrderByDescending(s => s.Started).FirstOrDefault();
            Select(first);
            _fast.Start();
            _slow.Start();
        };
    }

    // ------------------------------------------------------------------ signals from App

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void FollowLive()
    {
        _follow = true;
        if (!IsVisible) Show();
        Rescan();
    }

    // ------------------------------------------------------------------ data

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
        // Recordings that are still running (or were, a moment ago) keep changing.
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
        if (_current == null) return;
        if (_current.Done && _current.Partial.Length == 0 && !_current.IsLive && _partialRun == null) return;
        if (_current.Refresh()) Render(full: false);
    }

    private bool Matches(Session s) =>
        _query.Length == 0 || s.FullText.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
                           || s.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase);

    // ------------------------------------------------------------------ selection

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

    // ------------------------------------------------------------------ rendering

    private void Render(bool full)
    {
        var s = _current;
        UpdateHeader();
        if (s == null)
        {
            Doc.Document.Blocks.Clear();
            ResetRenderState();
            return;
        }

        bool atBottom = full ? s.IsLive : IsAtBottom();
        if (full)
        {
            Doc.Document.Blocks.Clear();
            ResetRenderState();
        }

        RemovePartial();
        for (; _rendered < s.Segments.Count; _rendered++)
            AddSegment(s.Segments[_rendered]);
        if (s.IsLive) AddPartial(s);

        if (Doc.Document.Blocks.Count == 0)
            Doc.Document.Blocks.Add(new Paragraph(new Run(EmptyDocText(s))) { Foreground = MutedBrush, Margin = new Thickness(0) });

        if (atBottom) Doc.ScrollToEnd();
        else if (full)
        {
            Doc.ScrollToHome();
            if (_query.Length > 0) ScrollToFirstHit();
        }
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
        _lastPara = null;
        _partialRun = null;
        _partialPara = null;
    }

    private void AddSegment(Segment seg)
    {
        // Placeholder text ("Listening…") goes away once real text arrives.
        if (_rendered == 0) Doc.Document.Blocks.Clear();

        bool joins = _lastPara != null && seg.Start - _lastParaEnd < 2.0 && seg.End - _lastParaStart < 60;
        if (joins)
        {
            AddText(_lastPara!, " " + seg.Text);
            _lastParaEnd = seg.End;
            return;
        }
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 20) };
        p.Inlines.Add(new Run(Session.FormatTime(seg.Start))
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = FaintBrush,
            FontFamily = new FontFamily("Segoe UI"),
        });
        p.Inlines.Add(new LineBreak());
        AddText(p, seg.Text);
        Doc.Document.Blocks.Add(p);
        _lastPara = p;
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

    private void AddPartial(Session s)
    {
        if (s.Partial.Length == 0) return;
        if (_rendered == 0) Doc.Document.Blocks.Clear();
        _partialRun = new Run(s.Partial) { Foreground = MutedBrush };
        // Continue the current paragraph if the speaker is still going; otherwise start a new one.
        if (_lastPara != null && s.Duration - _lastParaEnd < 6)
        {
            _partialRun.Text = " " + s.Partial;
            _lastPara.Inlines.Add(_partialRun);
        }
        else
        {
            _partialPara = new Paragraph { Margin = new Thickness(0, 0, 0, 20) };
            _partialPara.Inlines.Add(new Run("Now")
            {
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = LiveBrush,
            });
            _partialPara.Inlines.Add(new LineBreak());
            _partialPara.Inlines.Add(_partialRun);
            Doc.Document.Blocks.Add(_partialPara);
        }
    }

    private void RemovePartial()
    {
        if (_partialPara != null) Doc.Document.Blocks.Remove(_partialPara);
        else if (_partialRun != null && _lastPara != null) _lastPara.Inlines.Remove(_partialRun);
        _partialPara = null;
        _partialRun = null;
        // Drop the "Listening…" placeholder before real content is added.
        if (_rendered == 0) Doc.Document.Blocks.Clear();
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
            bool loading = s.LiveStatus == "loading";
            bool finishing = s.LiveStatus == "finishing";
            SetChip(loading ? "Loading model" : finishing ? "Finishing" : "Live",
                    loading || finishing ? AmberBrush : LiveBrush, loading || finishing ? AmberSoftBrush : LiveSoftBrush);
        }
        else if (s.Error != null) SetChip("Transcription failed", LiveBrush, GreySoftBrush);
        else StatusChip.Visibility = Visibility.Collapsed;

        bool audio = File.Exists(s.WavPath);
        PlayButton.IsEnabled = audio && !s.IsLive;
        FolderButton.IsEnabled = audio || File.Exists(s.JsonlPath);
        CopyButton.IsEnabled = ExportButton.IsEnabled = s.Segments.Count > 0;
    }

    private void SetChip(string text, Brush fg, Brush bg)
    {
        StatusChip.Visibility = Visibility.Visible;
        StatusChip.Background = bg;
        StatusDot.Fill = fg;
        StatusText.Foreground = fg;
        StatusText.Text = text;
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
            SidebarEmpty.Text = "No transcripts yet. Start a recording in RSP Audio Recorder to see the live transcript.";
            SidebarEmpty.Visibility = Visibility.Visible;
        }
        else if (_query.Length > 0 && _view.Count == 0)
        {
            SidebarEmpty.Text = $"No transcripts mention “{_query}”.";
            SidebarEmpty.Visibility = Visibility.Visible;
        }
        else SidebarEmpty.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ search

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

    // ------------------------------------------------------------------ actions

    private string PlainText(Session s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(s.Title);
        sb.AppendLine();
        double paraStart = -1, paraEnd = -100;
        var para = new StringBuilder();
        void Flush()
        {
            if (para.Length == 0) return;
            sb.Append('[').Append(Session.FormatTime(paraStart)).Append("] ").AppendLine(para.ToString().Trim());
            sb.AppendLine();
            para.Clear();
        }
        foreach (var seg in s.Segments)
        {
            if (seg.Start - paraEnd >= 2.0 || seg.End - paraStart >= 60) { Flush(); paraStart = seg.Start; }
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
            InitialDirectory = TranscriptStore.Folder,
            Filter = "Text file (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, PlainText(_current), Encoding.UTF8);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
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

    // ------------------------------------------------------------------ chrome

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void TintTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int caption = 0x00FBF7F5;   // #F5F7FB as COLORREF (0x00BBGGRR) — matches the sidebar
        int text = 0x00332017;      // #172033
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
