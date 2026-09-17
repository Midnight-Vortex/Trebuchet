using System.CommandLine;
using System.CommandLine.Parsing;
using TrebuchetLib;

namespace Boulder.Commands;

public static class RootCommandFactory
{
    public static RootCommand Create()
    {
        var command = new RootCommand("Boulder - Trebuchet's CLI");
        command.Add(LambCommand.Command);
        command.Add(KillCommand.Command);
        foreach (var flag in new[]
                 {
                     Constants.argLive, Constants.argEnhanced,
                     Constants.argPtc, Constants.argExperiment
                 })
        {
            command.Add(new Option<bool>(flag) { Recursive = true });
        }
        AddEditionValidation(command);
        return command;
    }

    private static void AddEditionValidation(Command command)
    {
        // System.CommandLine runs validators on the invoked command, not its ancestors.
        if (!command.Validators.Contains(ValidateEdition)) command.Validators.Add(ValidateEdition);
        foreach (var child in command.Subcommands) AddEditionValidation(child);
    }

    private static void ValidateEdition(CommandResult result)
    {
        if (result.GetValue<bool>(Constants.argLive) &&
            (result.GetValue<bool>(Constants.argPtc) || result.GetValue<bool>(Constants.argEnhanced)))
            result.AddError("--live cannot be combined with --enhanced or --ptc.");
    }
}
