using System.Diagnostics.CodeAnalysis;

namespace tot_lib.CommandLine;


internal class Command<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]TCommand> : System.CommandLine.Command where TCommand : class,ICommand<TCommand>
{
    public Command(string name, string? description = null) : base(name, description)
    {
    }
}
