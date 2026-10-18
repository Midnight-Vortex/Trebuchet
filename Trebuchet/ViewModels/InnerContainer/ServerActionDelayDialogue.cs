using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Trebuchet.Assets;

namespace Trebuchet.ViewModels.InnerContainer;

/// <summary>
/// Asks when a server should stop or restart: now, after one of the preset delays or after a delay in minutes.
/// Also asks how long the automatic restarts pause (other labels, "now" is "no pause").
/// </summary>
public class ServerActionDelayDialogue : DialogueContent
{
    private decimal? _customMinutes = 20;

    public ServerActionDelayDialogue(string title, string description, IEnumerable<int> presetMinutes,
        bool includeNow = true, string? nowLabel = null, string? confirmLabel = null)
    {
        Title = title;
        Description = description;
        ConfirmLabel = confirmLabel ?? Resources.DelayStart;
        if (includeNow)
            Presets.Add(new DelayPreset(nowLabel ?? Resources.DelayNow, TimeSpan.Zero, Choose));
        foreach (var minutes in presetMinutes)
            Presets.Add(new DelayPreset(FormatMinutes(minutes), TimeSpan.FromMinutes(minutes), Choose));
        ConfirmCustom = ReactiveCommand.Create(() => Choose(TimeSpan.FromMinutes((double)(CustomMinutes ?? 0))),
            this.WhenAnyValue(x => x.CustomMinutes, minutes => minutes is >= 1 and <= MaximumMinutes));
        CancelCommand = ReactiveCommand.Create(Close);
    }

    public const int MaximumMinutes = 1440;

    public string Title { get; }
    public string Description { get; }
    public string ConfirmLabel { get; }
    public ObservableCollection<DelayPreset> Presets { get; } = [];
    public ReactiveCommand<Unit, Unit> ConfirmCustom { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>The chosen delay, null when the dialogue was cancelled.</summary>
    public TimeSpan? Result { get; private set; }

    public decimal? CustomMinutes
    {
        get => _customMinutes;
        set => this.RaiseAndSetIfChanged(ref _customMinutes, value);
    }

    public static string FormatMinutes(int minutes)
    {
        return minutes >= 60 && minutes % 60 == 0
            ? string.Format(Resources.DelayHours, minutes / 60)
            : string.Format(Resources.DelayMinutes, minutes);
    }

    private void Choose(TimeSpan delay)
    {
        Result = delay;
        Close();
    }
}

public class DelayPreset(string label, TimeSpan delay, Action<TimeSpan> choose)
{
    public string Label { get; } = label;
    public TimeSpan Delay { get; } = delay;
    public bool IsNow => Delay == TimeSpan.Zero;
    public ReactiveCommand<Unit, Unit> Command { get; } = ReactiveCommand.Create(() => choose(delay));
}
