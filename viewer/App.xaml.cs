using System.Threading;
using System.Windows;

namespace RSPTranscripts;

/// <summary>
/// Single instance. Arguments:
///   (none)     show the window
///   --follow   a recording just started: open (without stealing focus) and follow the live transcript
/// A second launch signals the running instance and exits.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\RSPTranscripts.Instance";
    private const string ShowEventName = @"Local\RSPTranscripts.Show";
    private const string FollowEventName = @"Local\RSPTranscripts.Follow";

    private Mutex? _mutex;
    private EventWaitHandle? _show, _follow;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool follow = e.Args.Any(a => a.Equals("--follow", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(follow ? FollowEventName : ShowEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _follow = new EventWaitHandle(false, EventResetMode.AutoReset, FollowEventName);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "RSP Transcripts", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        base.OnStartup(e);
        var win = new MainWindow(follow);
        MainWindow = win;
        if (follow) win.ShowActivated = false;
        win.Show();

        var listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _show, _follow };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(() =>
                {
                    if (i == 0) win.BringToFront();
                    else win.FollowLive();
                });
            }
        }) { IsBackground = true, Name = "instance-signal" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _show?.Dispose();
        _follow?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
