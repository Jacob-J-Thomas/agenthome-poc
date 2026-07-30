using EmbodySense.Cli.Command;
namespace EmbodySense.Cli.Command;

/// <summary>
/// Provides case-insensitive token, flag, option, and operand queries over one immutable CLI argument sequence.
/// </summary>
public sealed class CliArguments
{
    private readonly string[] _args;

    /// <summary>
    /// Initializes an argument reader over the supplied token array.
    /// </summary>
    /// <param name="args">The CLI tokens in their original order.</param>
    public CliArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        _args = args;
    }

    /// <summary>
    /// Gets the number of supplied tokens.
    /// </summary>
    public int Count => _args.Length;

    /// <summary>
    /// Gets the first token trimmed and normalized to lowercase, or <see langword="null"/> when no token exists.
    /// </summary>
    public string? Command => At(0)?.Trim().ToLowerInvariant();

    /// <summary>
    /// Gets the token at an index without throwing for an out-of-range index.
    /// </summary>
    /// <param name="index">The zero-based token index.</param>
    /// <returns>The original token, or <see langword="null"/> when the index is outside the sequence.</returns>
    public string? At(int index)
    {
        return index >= 0 && index < _args.Length ? _args[index] : null;
    }

    /// <summary>
    /// Determines whether a token is one of the supported help spellings.
    /// </summary>
    /// <param name="index">The zero-based token index.</param>
    /// <returns><see langword="true"/> for <c>help</c>, <c>--help</c>, or <c>-h</c>, ignoring case.</returns>
    public bool IsHelpAt(int index)
    {
        return IsHelpToken(At(index));
    }

    /// <summary>
    /// Compares a token with an expected value using ordinal, case-insensitive equality.
    /// </summary>
    /// <param name="index">The zero-based token index.</param>
    /// <param name="token">The expected token.</param>
    /// <returns><see langword="true"/> when the indexed token matches.</returns>
    public bool IsTokenAt(int index, string token)
    {
        return TokenEquals(At(index), token);
    }

    /// <summary>
    /// Determines whether a token matches any supplied value.
    /// </summary>
    /// <param name="index">The zero-based token index.</param>
    /// <param name="tokens">The accepted token values.</param>
    /// <returns><see langword="true"/> when any value matches using ordinal, case-insensitive equality.</returns>
    public bool IsAnyTokenAt(int index, params string[] tokens)
    {
        return tokens.Any(token => IsTokenAt(index, token));
    }

    /// <summary>
    /// Finds the value immediately following the first matching option at or after an index.
    /// </summary>
    /// <param name="optionName">The option name to find.</param>
    /// <param name="startIndex">The first token index to inspect.</param>
    /// <returns>The option value, or <see langword="null"/> when the option is absent.</returns>
    /// <exception cref="ArgumentException">The option is present without a following non-option value.</exception>
    public string? OptionValue(string optionName, int startIndex = 1)
    {
        for (var i = startIndex; i < _args.Length; i++)
        {
            if (TokenEquals(_args[i], optionName))
            {
                return RequireOptionValue(optionName, i);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the first occurrence of any supplied option and returns its following value.
    /// </summary>
    /// <param name="optionNames">Equivalent option spellings in no precedence order beyond their position in the input.</param>
    /// <returns>The value of the earliest matching option, or <see langword="null"/> when none is present.</returns>
    /// <exception cref="ArgumentException">The selected option has no following non-option value.</exception>
    public string? OptionValueInTokenOrder(params string[] optionNames)
    {
        for (var i = 1; i < _args.Length; i++)
        {
            if (optionNames.Any(optionName => TokenEquals(_args[i], optionName)))
            {
                return RequireOptionValue(_args[i], i);
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether a flag occurs at or after an index.
    /// </summary>
    /// <param name="flagName">The flag to find.</param>
    /// <param name="startIndex">The first token index to inspect.</param>
    /// <returns><see langword="true"/> when the flag is present using ordinal, case-insensitive equality.</returns>
    public bool HasFlag(string flagName, int startIndex = 1)
    {
        return _args.Skip(startIndex).Any(arg => TokenEquals(arg, flagName));
    }

    /// <summary>
    /// Returns the first positional operand after skipping selected tokens and option-value pairs.
    /// </summary>
    /// <param name="startIndex">The first token index to inspect.</param>
    /// <param name="ignoredTokens">Optional standalone tokens that do not represent operands.</param>
    /// <param name="optionsWithValue">Optional option names whose following token is also skipped.</param>
    /// <returns>The first non-option operand, or <see langword="null"/> when none remains.</returns>
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

    /// <summary>
    /// Determines whether a value is one of the supported help tokens.
    /// </summary>
    /// <param name="value">The token to inspect.</param>
    /// <returns><see langword="true"/> for <c>help</c>, <c>--help</c>, or <c>-h</c>, ignoring case.</returns>
    public static bool IsHelpToken(string? value)
    {
        return TokenEquals(value, "help") || TokenEquals(value, "--help") || TokenEquals(value, "-h");
    }

    /// <summary>
    /// Determines whether a token uses option syntax.
    /// </summary>
    /// <param name="value">The non-null token to inspect.</param>
    /// <returns><see langword="true"/> when the token starts with a hyphen.</returns>
    public static bool IsOption(string value)
    {
        return value.StartsWith('-');
    }

    private static bool TokenEquals(string? left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private string RequireOptionValue(string optionName, int optionIndex)
    {
        var value = At(optionIndex + 1);
        if (value is null || IsOption(value))
        {
            throw new ArgumentException($"option {optionName} requires a value");
        }

        return value;
    }
}
