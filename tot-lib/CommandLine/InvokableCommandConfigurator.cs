using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace tot_lib.CommandLine;

internal class InvokableCommandConfigurator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> : 
    CommandConfigurator<TCommand>, ICommandServiceBuilder<TCommand> where TCommand : class,IInvokableCommand<TCommand>
{
    public InvokableCommandConfigurator(string name, string? description = null) : base(new InvokableCommand<TCommand>(name, description))
    {
        Command.SetAction(((InvokableCommand<TCommand>)Command).InvokeAsync);
    }
    
    public ICommandBuilder<TCommand> SetServiceConfiguration(Action<IServiceCollection> configuration)
    {
        ((InvokableCommand<TCommand>)Command).SetServiceConfiguration(configuration);
        return this;
    }
}
