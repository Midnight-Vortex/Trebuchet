using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

internal class ArgumentConfigurator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand, TValue>
    (CommandConfigurator<TCommand> commandConfigurator, Argument<TCommand, TValue> argument)
    : IArgument<TCommand, TValue>
    where TCommand : class,ICommand<TCommand>
{
    public IArgument<TCommand, TValue> SetSetter(Action<TCommand, TValue?> setter)
    {
        argument.SetSetter(setter);
        return this;
    }
    
    public IArgument<TCommand, TValue> SetDefault(TValue defaultValue)
    {
        argument.DefaultValueFactory = _ => defaultValue;
        return this;
    }

    public IArgument<TCommand, TValue> AddValidator(Action<ArgumentResult> validate)
    {
        argument.Validators.Add(validate);
        return this;
    }

    public ICommandBuilder<TCommand> BuildArgument()
    {
        commandConfigurator.Command.Add(argument);
        return commandConfigurator;
    }
}
