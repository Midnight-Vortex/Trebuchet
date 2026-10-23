using Avalonia;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ReactiveUI.Avalonia;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;
using tot_lib.OsSpecific;
using Trebuchet.Utils;
using TrebuchetLib;
using TrebuchetLib.OsSpecific;
using TrebuchetLib.Processes;

namespace Trebuchet;

static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Started only to send Ctrl+C to the console of a server (see ConsoleControl): nothing else of Trebuchet runs. It
        // must come first, before the single instance check that would send it to the running Trebuchet instead.
        if (ConsoleControl.IsHelperCall(args, out var serverPid))
        {
            Environment.ExitCode = ConsoleControl.RunHelper(serverPid);
            return;
        }
        ConsoleControl.UseHelper(Environment.ProcessPath);

        using var mutex = new Mutex(true, @"TotTrebuchet", out var createdNew);
        
        if(createdNew || args.Contains(AppConstants.RestartArg))
        {
            // Notes that this session runs, so a hard end (killed from outside, power loss, a native crash) shows as a
            // report at the next start: no code runs at such an end, so no crash report could be written then.
            SessionMarker.Begin(Constants.GetLoggingDirectory().FullName, tot_lib.ProcessUtil.GetStringVersion());
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        else
        {
            var currentProcess = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(currentProcess.ProcessName))
            {
                if (process.Id != currentProcess.Id)
                {
                    TrebuchetOsSpecificEx.GetOsPlatformSpecific().FocusWindow(process.MainWindowHandle);
                    break;
                }
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current
            .Register<MaterialDesignIconProvider>();
        
        return AppBuilder.Configure<App>()
            // An unhandled command/observable error is logged and shown instead of ending the application.
            .UseReactiveUI(builder => builder.WithExceptionHandler(
                System.Reactive.Observer.Create<Exception>(App.HandleReactiveError)))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}