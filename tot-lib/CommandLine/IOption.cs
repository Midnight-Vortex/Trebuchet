using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public interface IOption<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand, TValue> where TCommand : class,ICommand<TCommand>
{
    IOption<TCommand, TValue> AddAlias(string alias);
    IOption<TCommand, TValue> SetSetter(Action<TCommand, TValue?> setter);
    IOption<TCommand, TValue> SetDefault(TValue defaultValue);
    IOption<TCommand, TValue> AddValidator(Action<OptionResult> validate);
    IOption<TCommand, TValue> LegalFileNamesOnly();
    IOption<TCommand, TValue> LegalFilePathsOnly();
    ICommandBuilder<TCommand> BuildOption();
}
