using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

internal class OptionCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> : IOptionCollection<TCommand> where TCommand : class,ICommand<TCommand>
{
    private CommandConfigurator<TCommand> _commandConfigurator;
    
    public OptionCollection(CommandConfigurator<TCommand> commandConfigurator)
    {
        _commandConfigurator = commandConfigurator;
    }

    public IOption<TCommand, TValue> Create<TValue>(string name, string? description = null)
    {
        return new OptionConfigurator<TCommand,TValue>(_commandConfigurator, 
            new Option<TCommand, TValue>(name, description));
    }

    public IOption<TCommand, TValue> Create<TValue>(string[] aliases, string? description = null)
    {
        return new OptionConfigurator<TCommand,TValue>(_commandConfigurator, 
            new Option<TCommand, TValue>(aliases, description));
    }

    public IOption<TCommand, TValue> Create<TValue>(string name, Func<TValue> getDefaultValue, string? description = null)
    {
        return new OptionConfigurator<TCommand,TValue>(_commandConfigurator, 
            new Option<TCommand, TValue>(name, getDefaultValue, description));
    }

    public IOption<TCommand, TValue> Create<TValue>(string[] aliases, Func<TValue> getDefaultValue, string? description = null)
    {
        return new OptionConfigurator<TCommand,TValue>(_commandConfigurator, 
            new Option<TCommand, TValue>(aliases, getDefaultValue, description));
    }
}
