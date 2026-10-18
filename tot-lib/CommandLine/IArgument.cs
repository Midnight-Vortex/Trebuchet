using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public interface IArgument<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand, TValue> where TCommand : class,ICommand<TCommand>
{
    IArgument<TCommand, TValue> SetSetter(Action<TCommand, TValue?> setter);
    IArgument<TCommand, TValue> SetDefault(TValue defaultValue);
    IArgument<TCommand, TValue> AddValidator(Action<ArgumentResult> validate);
    ICommandBuilder<TCommand> BuildArgument();
}
