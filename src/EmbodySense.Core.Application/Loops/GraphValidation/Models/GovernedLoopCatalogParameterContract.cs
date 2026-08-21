namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Defines exact catalog-owned semantics for one executable descriptor parameter.</summary>
/// <param name="Id">The exact parameter identity.</param>
/// <param name="ValueKind">The canonical value semantics.</param>
/// <param name="Required">Whether the parameter must be declared by every matching node.</param>
/// <param name="MinimumCharacters">The inclusive minimum canonical character count.</param>
/// <param name="MaximumCharacters">The inclusive maximum canonical character count.</param>
/// <param name="MinimumInteger">The inclusive integer minimum when <paramref name="ValueKind"/> is <see cref="GovernedLoopParameterValueKind.Integer"/>.</param>
/// <param name="MaximumInteger">The inclusive integer maximum when <paramref name="ValueKind"/> is <see cref="GovernedLoopParameterValueKind.Integer"/>.</param>
/// <param name="AllowedValues">The exact ordinal allowed values when <paramref name="ValueKind"/> is <see cref="GovernedLoopParameterValueKind.Enumeration"/>.</param>
/// <param name="MaximumUtf8Bytes">An optional inclusive UTF-8 byte ceiling in addition to the character ceiling.</param>
/// <param name="AllowLeadingOption">Whether a value beginning with a hyphen is admitted.</param>
/// <param name="AllowResponseFileReference">Whether a value beginning with an at sign is admitted.</param>
public sealed record GovernedLoopCatalogParameterContract(
    string Id,
    GovernedLoopParameterValueKind ValueKind,
    bool Required,
    int MinimumCharacters,
    int MaximumCharacters,
    long? MinimumInteger,
    long? MaximumInteger,
    IReadOnlyList<string> AllowedValues,
    int? MaximumUtf8Bytes = null,
    bool AllowLeadingOption = true,
    bool AllowResponseFileReference = true);
