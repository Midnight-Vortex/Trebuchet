using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Trebuchet.Assets;

namespace Trebuchet.ViewModels.DiscordBot;

/// <summary>One step of the setup help: a title, a few short paragraphs and an optional tip.</summary>
public sealed record DiscordBotHelpStep(int Number, string Title, IReadOnlyList<string> Paragraphs, string Hint)
{
    public bool HasHint => Hint.Length > 0;
    public string Label => string.Format(CultureInfo.CurrentCulture, Resources.DiscordBotHelpStepLabel, Number);
}

/// <summary>
/// The texts of the setup help window (resources, every UI language): what to click in the Discord developer portal, in
/// Discord and here, step by step. Paragraphs are separated by line breaks in the resource.
/// </summary>
public static class DiscordBotHelp
{
    public const int StepCount = 12;

    public static IReadOnlyList<string> Intro() => Paragraphs(Resources.DiscordBotHelpIntro);

    public static IReadOnlyList<DiscordBotHelpStep> Steps()
    {
        var steps = new List<DiscordBotHelpStep>(StepCount);
        for (var number = 1; number <= StepCount; number++)
        {
            steps.Add(new DiscordBotHelpStep(
                number,
                Text($"DiscordBotHelpStep{number}Title"),
                Paragraphs(Text($"DiscordBotHelpStep{number}Text")),
                Text($"DiscordBotHelpStep{number}Hint")));
        }
        return steps;
    }

    private static string Text(string key) => Resources.ResourceManager.GetString(key, Resources.Culture) ?? string.Empty;

    private static IReadOnlyList<string> Paragraphs(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>The data of the setup help window. Plain: no settings, no token, nothing from the bot.</summary>
public sealed class DiscordBotHelpViewModel : ReactiveObject
{
    public DiscordBotHelpViewModel(Func<Task> openOnlineGuide)
    {
        Title = Resources.DiscordBotHelpTitle;
        Intro = DiscordBotHelp.Intro();
        Steps = DiscordBotHelp.Steps();
        OpenOnlineGuide = ReactiveCommand.CreateFromTask(openOnlineGuide);
    }

    public string Title { get; }
    public IReadOnlyList<string> Intro { get; }
    public IReadOnlyList<DiscordBotHelpStep> Steps { get; }
    public ReactiveCommand<Unit, Unit> OpenOnlineGuide { get; }
}
