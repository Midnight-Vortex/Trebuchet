using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

internal class ArgumentCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand>(CommandConfigurator<TCommand> commandConfigurator) 
    : IArgumentCollection<TCommand> 
    where TCommand : class,ICommand<TCommand>
{
    public IArgument<TCommand, TValue> Create<TValue>(string name, string? description = null)
    {
        return new ArgumentConfigurator<TCommand, TValue>(commandConfigurator,
            new Argument<TCommand, TValue>(name, description));
    }

    public IArgument<TCommand, TValue> Create<TValue>(string name, Func<TValue> getDefaultValue, string? description = null)
    {
        return new ArgumentConfigurator<TCommand, TValue>(commandConfigurator,
            new Argument<TCommand, TValue>(name, getDefaultValue, description));
    }
}
