using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public static class CommandBuilder
{
    public static ICommandBuilder<TCommand> Create<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand>
        (string name, string? description = null) 
        where TCommand : class,ICommand<TCommand>
    {
        return new CommandConfigurator<TCommand>(name, description);
    }

    public static ICommandServiceBuilder<TCommand> CreateInvokable<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCommand>
        (string name, string? description = null) 
        where TCommand : class, IInvokableCommand<TCommand>
    {
        return new InvokableCommandConfigurator<TCommand>(name, description);
    }
}