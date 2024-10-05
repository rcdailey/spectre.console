namespace Spectre.Console.Cli;

internal class CommandFactory(ITypeResolver resolver)
{
    public ICommand Create(CommandInfo commandInfo)
    {
        if (commandInfo.Delegate != null)
        {
            return new DelegateCommand(commandInfo.Delegate);
        }

        if (resolver.Resolve(commandInfo.CommandType) is ICommand command)
        {
            return command;
        }

        throw CommandParseException.CouldNotCreateCommand(commandInfo.CommandType);
    }
}