using System.CommandLine;
using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

internal class Option<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand, TValue> : 
    System.CommandLine.Option<TValue>, IValueSetter<TCommand>
    where TCommand : class,ICommand<TCommand>
{
    public Option(string name, string? description = null) : base(name)
    {
        if (description is not null) Description = description;
    }

    public Option(string[] aliases, string? description = null) : base(aliases[0], aliases[1..])
    {
        if (description is not null) Description = description;
    }

    public Option(string name, Func<TValue> getDefaultValue, string? description = null) : base(name)
    {
        if (description is not null) Description = description;
        DefaultValueFactory = _ => getDefaultValue();
    }

    public Option(string[] aliases, Func<TValue> getDefaultValue, string? description = null) : base(aliases[0], aliases[1..])
    {
        if (description is not null) Description = description;
        DefaultValueFactory = _ => getDefaultValue();
    }
    
    private Action<TCommand, TValue?>? _setter;
    
    public void SetSetter(Action<TCommand, TValue?> action)
    {
        _setter = action;
    }
    
    public void SetValue(TCommand command, ParseResult parseResult)
    {
        _setter?.Invoke(command, parseResult.GetValue(this));
    }
}
