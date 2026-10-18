using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public interface ICommandCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> where TCommand : class,ICommand<TCommand>
{
    ICommandBuilder<TCommand> Add(System.CommandLine.Command command);
}