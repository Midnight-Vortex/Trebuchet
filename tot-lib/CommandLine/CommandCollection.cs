using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

internal class CommandCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand>(CommandConfigurator<TCommand> commandConfigurator) : ICommandCollection<TCommand> where TCommand : class,ICommand<TCommand>
{
    public ICommandBuilder<TCommand> Add(System.CommandLine.Command command)
    {
        commandConfigurator.Command.Add(command);
        return commandConfigurator;
    }
}
