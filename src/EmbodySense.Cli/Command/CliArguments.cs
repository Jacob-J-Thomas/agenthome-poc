namespace EmbodySense.Cli.Command;

internal sealed class CliArguments
{
    private readonly string[] _args;

    public CliArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        _args = args;
    }

    public int Count => _args.Length;

    public string? Command => At(0)?.Trim().ToLowerInvariant();

    public string? At(int index)
    {
        return index >= 0 && index < _args.Length ? _args[index] : null;
    }

    public bool IsHelpAt(int index)
    {
        return IsHelpToken(At(index));
    }

    public bool IsTokenAt(int index, string token)
    {
        return TokenEquals(At(index), token);
    }

    public bool IsAnyTokenAt(int index, params string[] tokens)
    {
        return tokens.Any(token => IsTokenAt(index, token));
    }

    public string? OptionValue(string optionName, int startIndex = 1)
    {
        for (var i = startIndex; i < _args.Length - 1; i++)
        {
            if (TokenEquals(_args[i], optionName))
            {
                return _args[i + 1];
            }
        }

        return null;
    }

    public string? OptionValueInTokenOrder(params string[] optionNames)
    {
        for (var i = 1; i < _args.Length - 1; i++)
        {
            if (optionNames.Any(optionName => TokenEquals(_args[i], optionName)))
            {
                return _args[i + 1];
            }
        }

        return null;
    }

    public bool HasFlag(string flagName, int startIndex = 1)
    {
        return _args.Skip(startIndex).Any(arg => TokenEquals(arg, flagName));
    }

    public string? FirstOperand(int startIndex, IReadOnlySet<string>? ignoredTokens = null, IReadOnlySet<string>? optionsWithValue = null)
    {
        for (var i = startIndex; i < _args.Length; i++)
        {
            var value = _args[i];

            if (ignoredTokens?.Contains(value) == true)
            {
                continue;
            }

            if (optionsWithValue?.Contains(value) == true)
            {
                i++;
                continue;
            }

            if (!IsOption(value))
            {
                return value;
            }
        }

        return null;
    }

    public static bool IsHelpToken(string? value)
    {
        return TokenEquals(value, "help") || TokenEquals(value, "--help") || TokenEquals(value, "-h");
    }

    public static bool IsOption(string value)
    {
        return value.StartsWith('-');
    }

    private static bool TokenEquals(string? left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
