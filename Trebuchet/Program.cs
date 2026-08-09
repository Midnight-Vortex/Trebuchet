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

namespace Trebuchet;

static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"TotTrebuchet", out var createdNew);
        
        if(createdNew || args.Contains(AppConstants.RestartArg))
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
            .UseReactiveUI(_ => { })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}