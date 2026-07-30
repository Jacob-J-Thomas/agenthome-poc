using EmbodySense.Core.Application.Runtime.Commands.Models;
namespace EmbodySense.Core.Application.Runtime.Commands;

/// <summary>
/// Represents a runtime command definition.
/// </summary>
public sealed record RuntimeCommandDefinition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeCommandDefinition"/> type.
    /// </summary>
    /// <param name="id">The ID.</param>
    /// <param name="aliases">The aliases.</param>
    /// <param name="description">The description.</param>
    /// <param name="helpAliases">The help aliases.</param>
    /// <param name="includeInHelp">The include in help.</param>
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

    /// <summary>
    /// Gets the runtime command ID.
    /// </summary>
    /// <value>The runtime command ID.</value>
    public RuntimeCommandId Id { get; }

    /// <summary>
    /// Gets the aliases text values.
    /// </summary>
    /// <value>The aliases text values.</value>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    /// <value>The description.</value>
    public string? Description { get; }

    /// <summary>
    /// Gets the help aliases text values.
    /// </summary>
    /// <value>The help aliases text values.</value>
    public IReadOnlyList<string> HelpAliases { get; }

    /// <summary>
    /// Gets a value indicating whether the include in help condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the include in help condition holds; otherwise, <see langword="false"/>.</value>
    public bool IncludeInHelp { get; }

    /// <summary>
    /// Determines whether the normalized input matches the expected runtime command definition.
    /// </summary>
    /// <param name="normalizedInput">The normalized input.</param>
    /// <returns><see langword="true"/> when matches; otherwise, <see langword="false"/>.</returns>
    public bool Matches(string normalizedInput)
    {
        return Aliases.Contains(NormalizeAlias(normalizedInput), StringComparer.Ordinal);
    }

    /// <summary>
    /// Formats the help line.
    /// </summary>
    /// <returns>The text value.</returns>
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
