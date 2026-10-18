using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public interface ICommand<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> where TCommand : class, ICommand<TCommand>
{
}

