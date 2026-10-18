using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReactiveUI;
using tot_lib;

namespace tot_gui_lib;

internal class CrashHandlerViewModel : ReactiveObject
{
    public CrashHandlerViewModel(Exception ex, List<CrashHandlerLog> logs)
    {
        _payload = new CrashHandlerPayload(ex)
        {
            Logs = logs
        };
        Title = ex.GetType().Name;
        Message = ex.Message;
        CallStack = ex.GetAllExceptions();

        _foldIcon = this.WhenAnyValue(x => x.FoldedCallstack)
            .Select(x => x ? @"mdi-chevron-right" : @"mdi-chevron-down")
            .ToProperty(this, x => x.FoldIcon);

        _windowHeight = this.WhenAnyValue(x => x.FoldedCallstack)
            .Select(x => x ? 300 : 600)
            .ToProperty(this, x => x.WindowHeight);
        var canSend = this.WhenAnyValue(x => x.ReportSent);

        SendReport = ReactiveCommand.CreateFromTask(SendReportAsync, canSend);
        SaveReport = ReactiveCommand.CreateFromTask(SaveReportAsync);
        FoldCallStack = ReactiveCommand.Create<Unit>((_) => FoldedCallstack = !FoldedCallstack);
        FoldedCallstack = true;
        HasReporter = CrashHandler.HasReportUri();
    }
    private bool _foldedCallstack;
    private bool _hasReporter;
    private bool _reportSent;
    private bool _sending;
    private CrashHandlerPayload _payload;
    private ObservableAsPropertyHelper<string> _foldIcon;
    private ObservableAsPropertyHelper<int> _windowHeight;

    public string Title { get; }
    public string Message { get; }
    public string CallStack { get; }
    public string FoldIcon => _foldIcon.Value;
    public int WindowHeight => _windowHeight.Value;
    
    public ReactiveCommand<Unit, Unit> FoldCallStack { get; }
    public ReactiveCommand<Unit, Unit> SendReport { get; }
    public ReactiveCommand<Unit, Unit> SaveReport { get; }
    
    public bool FoldedCallstack
    {
        get => _foldedCallstack;
        set => this.RaiseAndSetIfChanged(ref _foldedCallstack, value);
    }
    
    public bool ReportSent
    {
        get => _reportSent;
        set => this.RaiseAndSetIfChanged(ref _reportSent, value);
    }
    
    public bool Sending
    {
        get => _sending;
        set => this.RaiseAndSetIfChanged(ref _sending, value);
    }
    
    public bool HasReporter
    {
        get => _hasReporter;
        set => this.RaiseAndSetIfChanged(ref _hasReporter, value);
    }

    private async Task SendReportAsync()
    {
        ReportSent = true;
        Sending = true;
        var result = await CrashHandler.SendReport(_payload);
        Sending = false;
        ReportSent = result;
    }

    private async Task SaveReportAsync()
    {
        ReportSent = true;
        Sending = true;
        var json = JsonSerializer.Serialize(_payload, CrashHandlerPayloadJsonContext.Default.CrashHandlerPayload);
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow == null) return;

        var file = await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions()
        {
            Title = "Save Report",
            SuggestedFileName = "Report.json",
            FileTypeChoices = [new(@"Json Text")
            {
                MimeTypes = [@"application/json"],
                Patterns = [@"*.json"],
            }]
        });

        if (file is null) return;
        if (!file.Path.IsFile) return;
        var path = Path.GetFullPath(file.Path.LocalPath);
        await File.WriteAllTextAsync(path, json);
        Sending = false;
    }
}

public static class CrashHandler
{
    private static SemaphoreSlim? _semaphore;
    private static Uri? _reportSendUri;

    public static void SetReportUri(Uri uri) => _reportSendUri = uri;
    public static void SetReportUri(string url) => _reportSendUri = new Uri(url);
    public static bool HasReportUri() => _reportSendUri != null;

    public static Task Handle(Exception ex) => Handle(ex, []);
    public static async Task Handle(Exception ex, List<CrashHandlerLog> logs)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Handle(ex, logs);
            });
            return;
        }
        
        if (_semaphore is null)
            _semaphore = new SemaphoreSlim(1, 1);
        await _semaphore.WaitAsync();
        var handler = new CrashHandlerViewModel(ex, logs);
        var window = new CrashHandlerWindow();
        window.DataContext = handler;
        if (Application.Current is not null &&
            Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is not null)
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                await window.ShowDialog(desktop.MainWindow);
            }
            else
            {
                desktop.MainWindow = window;
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Show();
                await WaitForWindow(window);
            }

            _semaphore.Release();
            desktop.Shutdown(1);
            return;
        }

        throw new Exception("Not supported");
    }

    public static async Task<bool> SendReport(CrashHandlerPayload payload)
    {
        if (_reportSendUri == null) return false;
        
        using var httpClient = new HttpClient();
        using var response = await httpClient.PostAsJsonAsync(_reportSendUri, payload);
        return response.IsSuccessStatusCode;
    }

    private static async Task WaitForWindow(Window window)
    {
        while (window.IsActive)
        {
            await Task.Delay(25);
        }
    }
}