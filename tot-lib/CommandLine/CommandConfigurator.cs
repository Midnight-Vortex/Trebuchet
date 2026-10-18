using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace tot_lib.CommandLine;

internal class CommandConfigurator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> : ICommandBuilder<TCommand> where TCommand : class,ICommand<TCommand>
{
    public CommandConfigurator(string name, string? description = null) : this(new Command<TCommand>(name, description))
    {
    }

    protected CommandConfigurator(Command<TCommand> command)
    {
        Command = command;
        Options = new OptionCollection<TCommand>(this);
        Arguments = new ArgumentCollection<TCommand>(this);
        SubCommands = new CommandCollection<TCommand>(this);
    }

    public IOptionCollection<TCommand> Options { get; }
    public IArgumentCollection<TCommand> Arguments { get; }
    public ICommandCollection<TCommand> SubCommands { get; }
    public Command<TCommand> Command { get; }
    
    
    public System.CommandLine.Command BuildCommand()
    {
        return Command;
    }
}