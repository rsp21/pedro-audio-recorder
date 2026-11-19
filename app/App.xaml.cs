using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace RSPAudioRecorder;

/// <summary>
/// One background instance. Arguments:
///   (none)         show the window
///   --background   start hidden (Start with Windows)
///   --quit         close the running instance (also the earlier C++ recorder)
/// A second launch signals the running instance and exits.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\RSPAudioRecorder.Instance";
    private const string ShowEventName = @"Local\RSPAudioRecorder.ShowEvent";
    private const string QuitEventName = @"Local\RSPAudioRecorder.QuitEvent";

    private Mutex? _mutex;
    private EventWaitHandle? _show, _quit;
    private MainWindow? _main;

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

    protected override void OnStartup(StartupEventArgs e)
    {
        bool background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        bool quit = e.Args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase));
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            if (quit) QuitRunningInstance();
            else
            {
                AllowSetForegroundWindow(-1);
                try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            Shutdown();
            return;
        }
        if (quit) { Shutdown(); return; }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "RSP Audio Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        SessionEnding += (_, _) => _main?.SaveAndStop();

        base.OnStartup(e);
        AppSettings.InitStartup();
        _main = new MainWindow();
        _main.InitializeBackground();
        if (!background) _main.ShowFront();

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _show, _quit };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(() =>
                {
                    if (i == 0) _main.ShowFront();
                    else _main.QuitApp();
                });
                if (i == 1) return;
            }
        }) { IsBackground = true, Name = "instance-signal" };
        listener.Start();
    }

    /// <summary>Asks the running instance to quit and waits for it (used by the build script).</summary>
    private static void QuitRunningInstance()
    {
        var me = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id).ToList();
        try
        {
            using var ev = EventWaitHandle.OpenExisting(QuitEventName);
            ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The earlier C++ recorder: it listens for a registered window message.
            var hwnd = FindWindowW("RSPAudioRecorderWindow", null);
            if (hwnd != IntPtr.Zero) PostMessageW(hwnd, RegisterWindowMessageW("RSPAudioRecorder.Quit"), IntPtr.Zero, IntPtr.Zero);
        }
        foreach (var p in others)
        {
            try { p.WaitForExit(10000); } catch { }
            p.Dispose();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _show?.Dispose();
        _quit?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
