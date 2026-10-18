using System.CommandLine;
using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;

public class Argument<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand,TValue> : 
    System.CommandLine.Argument<TValue>, IValueSetter<TCommand>
    where TCommand : class,ICommand<TCommand>
{
    public Argument(string name, string? description = null) : base(name)
    {
        if (description is not null) Description = description;
    }

    public Argument(string name, Func<TValue> getDefaultValue, string? description = null) : base(name)
    {
        if (description is not null) Description = description;
        DefaultValueFactory = _ => getDefaultValue();
    }
    
    private Action<TCommand,TValue?>? _setter;
    
    public void SetSetter(Action<TCommand,TValue?> action)
    {
        _setter = action;
    }
    
    public void SetValue(TCommand command, ParseResult parseResult)
    {
        _setter?.Invoke(command, parseResult.GetValue(this));
    }
}
