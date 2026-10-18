using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public interface IInvokableCommand<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> : ICommand<TCommand> where TCommand : class,ICommand<TCommand>
{
    Task<int> InvokeAsync(CancellationToken ct);
}