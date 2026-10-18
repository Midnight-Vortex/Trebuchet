using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public interface ICommandBuilder<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> where TCommand : class,ICommand<TCommand>
{
    IOptionCollection<TCommand> Options { get; }
    IArgumentCollection<TCommand> Arguments { get; }
    ICommandCollection<TCommand> SubCommands { get; }
    System.CommandLine.Command BuildCommand();
}