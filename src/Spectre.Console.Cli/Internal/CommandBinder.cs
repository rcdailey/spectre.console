namespace Spectre.Console.Cli;

internal static class CommandBinder
{
    public static CommandSettings Bind(CommandTree? tree, Type settingsType, ITypeResolver resolver)
    {
        var lookup = CommandValueResolver.GetParameterValues(tree, resolver);

        // Got a constructor with at least one name corresponding to a settings?
        foreach (var constructor in settingsType.GetConstructors())
        {
            var parameters = constructor.GetParameters();
            if (parameters.Any(parameter => lookup.HasParameterWithName(parameter?.Name)))
            {
                return CommandConstructorBinder.CreateSettings(lookup, constructor, resolver);
            }
        }

        return CommandPropertyBinder.CreateSettings(lookup, settingsType, resolver);
    }
}