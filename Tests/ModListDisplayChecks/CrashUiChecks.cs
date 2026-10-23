using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvaloniaEdit;
using Humanizer;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;
using Trebuchet.Services;
using Trebuchet.ViewModels;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;
using TrebuchetLib.Services;

/// <summary>
/// Crash and hang regressions of the GUI. <see cref="Run"/> needs no Avalonia platform; <see cref="RunUi"/> needs the
/// platform of the console checks (an X display on Linux) and must run on its UI thread, before any await.
/// </summary>
public static class CrashUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        // Duration field: a number too large for a TimeSpan (or NaN) threw from the focus/Enter handler of the
        // control, an unhandled UI exception: Trebuchet restarted itself (or showed the crash window).
        var parse = typeof(tot_gui_lib.Controls.Duration).GetMethod("TryParseDuration", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new Exception("Duration.TryParseDuration not found");
        (bool ok, TimeSpan value) Parse(string text, TimeUnit unit)
        {
            var arguments = new object?[] { text, unit, null };
            var ok = (bool)parse.Invoke(null, arguments)!;
            return (ok, (TimeSpan)arguments[2]!);
        }
        Check(Parse("99999999", TimeUnit.Day) is { ok: false }, "A too large number of days is ignored instead of crashing");
        Check(Parse("-99999999", TimeUnit.Day) is { ok: false }, "A too large negative number of days is ignored");
        Check(Parse("1e300", TimeUnit.Second) is { ok: false }, "A too large number of seconds is ignored");
        Check(Parse(double.NaN.ToString(), TimeUnit.Hour) is { ok: false }, "NaN is ignored instead of crashing");
        Check(Parse("not a number", TimeUnit.Minute) is { ok: false }, "Invalid text is still ignored");
        Check(Parse(1.5.ToString(), TimeUnit.Hour) == (true, TimeSpan.FromMinutes(90))
              && Parse("30", TimeUnit.Minute) == (true, TimeSpan.FromMinutes(30))
              && Parse("2", TimeUnit.Day) == (true, TimeSpan.FromDays(2)), "Valid durations are still read in their unit");

        // Server download from the settings while Steam is busy (e.g. an automatic mod update started while the
        // instance count dialog was open): Steam throws OperationCanceledException, the settings field ignores it and
        // the progress overlay without a close button blocked the whole window until Trebuchet was restarted.
        {
            var steam = (Steam)RuntimeHelpers.GetUninitializedObject(typeof(Steam));
            SetField(steam, "_appSetup", new AppSetup(new Config { ServerInstanceCount = 1 }, GameEdition.Enhanced, false, false));
            SetField(steam, "_status", SteamStatus.UpdatingMods);
            var operations = (Operations)RuntimeHelpers.GetUninitializedObject(typeof(Operations));
            var box = new DialogueBox();
            SetField(operations, "_dialogueBox", box);
            SetField(operations, "_steam", steam);
            var download = operations.OnBoardingServerDownload();
            Check(download.IsCompleted, "Busy Steam fails the server download right away");
            Check(download.IsCanceled, "Busy Steam is reported to the caller as cancelled");
            Check(box.Popup is null && !box.Active, "A failed server download closes its progress overlay");
        }

        return count;
    }

    public static int RunUi()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        // Text editors bound to a text (ini tabs of the console, mod list editor, sequence actions): a new shorter
        // text threw for the caret behind its end, e.g. on every switch from an ini tab back to the log tabs.
        {
            var editor = new TextEditor();
            var behavior = new DocumentTextBindingBehavior();
            behavior.Attach(editor);
            behavior.Text = "[ServerSettings]\nMaxPlayers=40\n";
            editor.CaretOffset = editor.Document.TextLength;
            behavior.Text = string.Empty;
            Check(editor.Text.Length == 0 && editor.CaretOffset == 0, "Empty text after an edited ini tab keeps the caret valid");
            behavior.Text = "[Engine]\n";
            editor.CaretOffset = editor.Document.TextLength;
            behavior.Text = "[Game]";
            Check(editor.Text == "[Game]" && editor.CaretOffset == editor.Document.TextLength, "Shorter text clamps the caret");
            editor.CaretOffset = 2;
            behavior.Text = "[ServerSettings]";
            Check(editor.CaretOffset == 2, "The caret position is kept when it still fits");
            behavior.Detach();
        }

        // Crash window. The lifetime is never started: closing windows or Shutdown would end the checks' dispatcher.
        try { IconProvider.Current.Register<MaterialDesignIconProvider>(); }
        catch (Exception) { /* already registered */ }
        var app = Application.Current ?? throw new Exception("Avalonia is not set up");
        var lifetimeField = typeof(Application).GetField("_applicationLifetime", Private)
                            ?? throw new Exception("Application lifetime field not found");
        var previousLifetime = lifetimeField.GetValue(app);
        var desktop = new ClassicDesktopStyleApplicationLifetime();
        lifetimeField.SetValue(app, desktop);
        var opened = new List<Window>();
        try
        {
            // Crash while the main window is being built (e.g. a save that cannot be read) and no automatic restart
            // left: ShowDialog threw for the main window that was never shown. No crash window, no window at all,
            // the process stayed alive holding the single instance mutex.
            var notShown = new Window();
            desktop.MainWindow = notShown;
            var crash = tot_gui_lib.CrashHandler.Handle(new InvalidOperationException("Main window could not be built"));
            Pump(300);
            if (desktop.MainWindow is Window shown && shown != notShown) opened.Add(shown);
            Check(!crash.IsCompleted, "Crash handling waits on the crash window instead of failing: " + crash.Exception?.GetBaseException().Message);
            Check(desktop.MainWindow is tot_gui_lib.CrashHandlerWindow { IsVisible: true },
                "A crash window is shown when the main window was not shown yet");

            // Crash before any window (autostart with the edition argument): the crash window was closed together
            // with Trebuchet as soon as it was not the active window (shown in background, focus moved elsewhere).
            ResetCrashHandlerLock();
            desktop.MainWindow = null;
            var background = tot_gui_lib.CrashHandler.Handle(new InvalidOperationException("Edition could not be opened"));
            Pump(300);
            var crashWindow = desktop.MainWindow;
            if (crashWindow is not null) opened.Add(crashWindow);
            var other = new Window { Width = 200, Height = 100 };
            opened.Add(other);
            other.Show();
            Pump(300);
            Check(!background.IsCompleted && crashWindow is tot_gui_lib.CrashHandlerWindow { IsVisible: true },
                "The crash window stays open until it is closed, also without the focus");
        }
        finally
        {
            // Never closed through the crash handler: that shuts the application (and these checks) down.
            ResetCrashHandlerLock();
            foreach (var window in opened)
            {
                try { window.Hide(); } catch (Exception) { /* best effort */ }
            }
            lifetimeField.SetValue(app, previousLifetime);
        }

        return count;
    }

    private static void SetField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, Private) ?? throw new Exception($"Field {name} not found");
        field.SetValue(target, value);
    }

    /// <summary>The crash handler shows one crash window at a time; the checks leave theirs open.</summary>
    private static void ResetCrashHandlerLock()
    {
        typeof(tot_gui_lib.CrashHandler).GetField("_semaphore", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, null);
    }

    /// <summary>Runs the dispatcher (window events, focus changes, continuations) for a while.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(milliseconds));
        Dispatcher.UIThread.PushFrame(frame);
    }
}
