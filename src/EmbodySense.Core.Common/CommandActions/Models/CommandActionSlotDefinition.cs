namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Declares one server-owned typed value slot.</summary>
/// <param name="Name">The canonical slot name.</param>
/// <param name="Kind">The closed slot kind.</param>
/// <param name="MaxUtf8Bytes">The exact per-value UTF-8 ceiling.</param>
/// <param name="MinimumInteger">The inclusive integer minimum only for integer slots.</param>
/// <param name="MaximumInteger">The inclusive integer maximum only for integer slots.</param>
/// <param name="EnumerationValues">The ordered closed values only for enumeration slots.</param>
/// <param name="AllowLeadingOption">Whether a value beginning with <c>-</c> is an explicitly admitted complete token.</param>
public sealed record CommandActionSlotDefinition(
    string Name,
    CommandActionSlotKind Kind,
    int MaxUtf8Bytes,
    long? MinimumInteger,
    long? MaximumInteger,
    IReadOnlyList<string> EnumerationValues,
    bool AllowLeadingOption)
{
    /// <summary>Gets a defensive immutable copy of the enumeration values.</summary>
    public IReadOnlyList<string> EnumerationValues { get; } = EnumerationValues is null ? null! : Array.AsReadOnly(EnumerationValues.ToArray());
}
