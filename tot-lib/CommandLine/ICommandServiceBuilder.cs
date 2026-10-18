using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace tot_lib.CommandLine;

public interface ICommandServiceBuilder<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> where TCommand : class,ICommand<TCommand>
{
    ICommandBuilder<TCommand> SetServiceConfiguration(Action<IServiceCollection> configuration);
}