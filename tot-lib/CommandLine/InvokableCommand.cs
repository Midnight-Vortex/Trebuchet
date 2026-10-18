using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace tot_lib.CommandLine;

internal class InvokableCommand<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> : Command<TCommand> where TCommand : class, IInvokableCommand<TCommand>
{
    private Action<IServiceCollection>? _serviceConfiguration;

    public InvokableCommand(string name, string? description = null) : base(name, description)
    {
    }

    public void SetServiceConfiguration(Action<IServiceCollection> serviceConfiguration)
    {
        _serviceConfiguration = serviceConfiguration;
    }

    public Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (_serviceConfiguration is null)
            throw new NullReferenceException("Service configuration is missing");

        var collection = new ServiceCollection();
        _serviceConfiguration.Invoke(collection);
        collection.AddTransient(typeof(TCommand));
        var provider = collection.BuildServiceProvider();

        var command = provider.GetRequiredService(typeof(TCommand)) as TCommand;
        if (command is null)
            throw new NullReferenceException("Command failed to initialize");
        
        foreach (var option in Options)
            if(option is IValueSetter<TCommand> setter)
                setter.SetValue(command, parseResult);
        foreach (var argument in Arguments)
            if(argument is IValueSetter<TCommand> setter)
                setter.SetValue(command, parseResult);
        
        return command.InvokeAsync(cancellationToken);
    }
}
