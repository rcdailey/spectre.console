namespace Spectre.Console.Cli;

internal sealed class CommandExecutor
{
    private readonly CommandModel _model;
    private readonly List<IHelpProvider> _helpProviders;
    private readonly List<ICommandInterceptor> _commandInterceptors;
    private readonly CommandFactory _commandFactory;

    public CommandExecutor(
        CommandModel model,
        List<IHelpProvider> helpProviders,
        List<ICommandInterceptor> commandInterceptors,
        CommandFactory commandFactory)
    {
        _model = model;
        _helpProviders = helpProviders;
        _commandInterceptors = commandInterceptors;
        _commandFactory = commandFactory;
    }

    public async Task<int> Execute(CommandAppSettings appSettings, IEnumerable<string> args)
    {
        var arguments = args.ToSafeReadOnlyList();

        // No default command?
        if (_model.DefaultCommand == null)
        {
            // Got at least one argument?
            var firstArgument = arguments.FirstOrDefault();
            if (firstArgument != null)
            {
                // Asking for version? Kind of a hack, but it's alright.
                // We should probably make this a bit better in the future.
                if (firstArgument.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                    firstArgument.Equals("-v", StringComparison.OrdinalIgnoreCase))
                {
                    if (appSettings.ApplicationVersion != null)
                    {
                        var console = appSettings.Console.GetConsole();
                        console.MarkupLine(appSettings.ApplicationVersion);
                        return 0;
                    }
                }
            }
        }

        // Parse and map the model against the arguments.
        var parsedResult = ParseCommandLineArguments(_model, appSettings, arguments);

        // Get the registered help provider, falling back to the default provider
        // if no custom implementations have been registered.
        var helpProvider = _helpProviders.LastOrDefault() ?? new HelpProvider(appSettings);

        // Currently the root?
        if (parsedResult?.Tree == null)
        {
            // Display help.
            appSettings.Console.SafeRender(helpProvider.Write(_model, null));
            return 0;
        }

        // Get the command to execute.
        var leaf = parsedResult.Tree.GetLeafCommand();
        if (leaf.Command.IsBranch || leaf.ShowHelp)
        {
            // Branches can't be executed. Show help.
            appSettings.Console.SafeRender(helpProvider.Write(_model, leaf.Command));
            return leaf.ShowHelp ? 0 : 1;
        }

        // Is this the default and is it called without arguments when there are required arguments?
        if (leaf.Command.IsDefaultCommand && arguments.Count == 0 && leaf.Command.Parameters.Any(p => p.Required))
        {
            // Display help for default command.
            appSettings.Console.SafeRender(helpProvider.Write(_model, leaf.Command));
            return 1;
        }

        // Create the content.
        var context = new CommandContext(
            arguments,
            parsedResult.Remaining,
            leaf.Command.Name,
            leaf.Command.Data);

        // Execute the command tree.
        return await Execute(leaf, parsedResult.Tree, context, appSettings).ConfigureAwait(false);
    }

    private CommandTreeParserResult ParseCommandLineArguments(CommandModel model, CommandAppSettings settings, IReadOnlyList<string> args)
    {
        var parser = new CommandTreeParser(model, settings.CaseSensitivity, settings.ParsingMode, settings.ConvertFlagsToRemainingArguments);

        var parserContext = new CommandTreeParserContext(args, settings.ParsingMode);
        var tokenizerResult = CommandTreeTokenizer.Tokenize(args);
        var parsedResult = parser.Parse(parserContext, tokenizerResult);

        var lastParsedLeaf = parsedResult.Tree?.GetLeafCommand();
        var lastParsedCommand = lastParsedLeaf?.Command;
        if (lastParsedLeaf != null && lastParsedCommand != null &&
            lastParsedCommand.IsBranch && !lastParsedLeaf.ShowHelp &&
            lastParsedCommand.DefaultCommand != null)
        {
            // Insert this branch's default command into the command line
            // arguments and try again to see if it will parse.
            var argsWithDefaultCommand = new List<string>(args);

            argsWithDefaultCommand.Insert(tokenizerResult.Tokens.Position, lastParsedCommand.DefaultCommand.Name);

            parserContext = new CommandTreeParserContext(argsWithDefaultCommand, settings.ParsingMode);
            tokenizerResult = CommandTreeTokenizer.Tokenize(argsWithDefaultCommand);
            parsedResult = parser.Parse(parserContext, tokenizerResult);
        }

        return parsedResult;
    }

    private async Task<int> Execute(
        CommandTree leaf,
        CommandTree tree,
        CommandContext context,
        CommandAppSettings appSettings)
    {
        try
        {
            // Bind the command tree against the settings.
            var commandSettings = CommandBinder.Bind(tree, leaf.Command.SettingsType, resolver);

#pragma warning disable CS0618 // Type or member is obsolete
            if (appSettings.Interceptor != null)
            {
                _commandInterceptors.Add(appSettings.Interceptor);
            }
#pragma warning restore CS0618 // Type or member is obsolete

            foreach (var interceptor in _commandInterceptors)
            {
                interceptor.Intercept(context, commandSettings);
            }

            // Create and validate the command.
            var command = leaf.CreateCommand(resolver);
            var validationResult = command.Validate(context, commandSettings);
            if (!validationResult.Successful)
            {
                throw CommandRuntimeException.ValidationFailed(validationResult);
            }

            // Execute the command.
            var result = await command.Execute(context, commandSettings);
            foreach (var interceptor in _commandInterceptors)
            {
                interceptor.InterceptResult(context, commandSettings, ref result);
            }

            return result;
        }
        catch (Exception ex) when (appSettings is { ExceptionHandler: not null, PropagateExceptions: false })
        {
            return appSettings.ExceptionHandler(ex, resolver);
        }
    }
}