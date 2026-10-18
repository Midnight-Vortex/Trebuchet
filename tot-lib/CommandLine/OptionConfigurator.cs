using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

internal class OptionConfigurator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand, TValue>(CommandConfigurator<TCommand> commandConfigurator, Option<TCommand, TValue> option)
    : IOption<TCommand, TValue>
    where TCommand : class,ICommand<TCommand>
{
    public IOption<TCommand, TValue> AddAlias(string alias)
    {
        option.Aliases.Add(alias);
        return this;
    }

    public IOption<TCommand, TValue> SetSetter(Action<TCommand, TValue?> setter)
    {
        option.SetSetter(setter);
        return this;
    }

    public IOption<TCommand, TValue> SetDefault(TValue defaultValue)
    {
        option.DefaultValueFactory = _ => defaultValue;
        return this;
    }

    public IOption<TCommand, TValue> AddValidator(Action<OptionResult> validate)
    {
        option.Validators.Add(validate);
        return this;
    }

    public IOption<TCommand, TValue> LegalFileNamesOnly()
    {
        option.AcceptLegalFileNamesOnly();
        return this;
    }

    public IOption<TCommand, TValue> LegalFilePathsOnly()
    {
        option.AcceptLegalFilePathsOnly();
        return this;
    }

    public ICommandBuilder<TCommand> BuildOption()
    {
        commandConfigurator.Command.Add(option);
        return commandConfigurator;
    }
}
