using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public interface IOptionCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> where TCommand : class,ICommand<TCommand>
{
    IOption<TCommand, TValue> Create<TValue>(string name, string? description = null);

    IOption<TCommand, TValue> Create<TValue>(string[] aliases, string? description = null);

    IOption<TCommand, TValue> Create<TValue>(string name, Func<TValue> getDefaultValue, string? description = null);

    IOption<TCommand, TValue> Create<TValue>(string[] aliases, Func<TValue> getDefaultValue, string? description = null);
}
