namespace EmbodySense.Core.Application.Runtime.Commands;

public sealed record RuntimeCommandDefinition
{
    public RuntimeCommandDefinition(
        RuntimeCommandId id,
        IReadOnlyList<string> aliases,
        string? description = null,
        IReadOnlyList<string>? helpAliases = null,
        bool includeInHelp = true)
    {
        if (!Enum.IsDefined(id) || id == RuntimeCommandId.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Choose a concrete runtime command id.");
        }

        ArgumentNullException.ThrowIfNull(aliases);
        if (aliases.Count == 0)
        {
            throw new ArgumentException("At least one alias is required.", nameof(aliases));
        }

        Id = id;
        Aliases = aliases.Select(NormalizeAlias).ToArray();
        Description = description;
        HelpAliases = (helpAliases ?? aliases).Select(alias => alias.Trim()).Where(alias => alias.Length > 0).ToArray();
        IncludeInHelp = includeInHelp;
    }

    public RuntimeCommandId Id { get; }

    public IReadOnlyList<string> Aliases { get; }

    public string? Description { get; }

    public IReadOnlyList<string> HelpAliases { get; }

    public bool IncludeInHelp { get; }

    public bool Matches(string normalizedInput)
    {
        return Aliases.Contains(NormalizeAlias(normalizedInput), StringComparer.Ordinal);
    }

    public string FormatHelpLine()
    {
        if (string.IsNullOrWhiteSpace(Description))
        {
            return string.Join(", ", HelpAliases);
        }

        return $"{string.Join(", ", HelpAliases)} - {Description}";
    }

    private static string NormalizeAlias(string alias)
    {
        return alias.Trim().ToLowerInvariant();
    }
}
